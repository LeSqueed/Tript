// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Serilog;
using Tript.Obs;

namespace Tript.Recorder;

internal sealed class MuxerOutput : IRecorderOutput, IReplayBufferOutput
{
    private readonly ObsOutput _output;
    private readonly ObsEncoder? _ownedVideoEncoder;
    private readonly ObsEncoder? _audioEncoder;
    private readonly AudioRouting? _audioRouting;
    private readonly bool _isReplayBuffer;
    private readonly object _replayGate = new();
    private readonly ManualResetEventSlim _replaySaveCompleted = new(true);
    private Action<string>? _replaySaved;
    private bool _replaySavePending;

    internal MuxerOutput(ObsOutput output, ObsEncoder? ownedVideoEncoder, ObsEncoder? audioEncoder,
        AudioRouting? audioRouting, bool isReplayBuffer)
    {
        _output = output;
        _ownedVideoEncoder = ownedVideoEncoder;
        _audioEncoder = audioEncoder;
        _audioRouting = audioRouting;
        _isReplayBuffer = isReplayBuffer;
        if (_isReplayBuffer)
            _output.Saved += OnReplaySaved;
    }

    public bool IsActive => _output.IsActive;

    public bool Start() => _output.Start();

    public void Stop() => _output.Stop();

    public bool WaitForStop(TimeSpan timeout) => _output.WaitForStop(timeout);

    public string? LastError => _output.LastError;

    public event EventHandler<ObsOutputStopEvent>? Stopped
    {
        add => _output.Stopped += value;
        remove => _output.Stopped -= value;
    }

    public bool SaveReplay(string directory, string format, Action<string> onSaved)
    {
        if (!_isReplayBuffer)
            return false;

        lock (_replayGate)
        {
            if (_replaySavePending)
                return false;

            using var settings = _output.GetSettings();
            settings.SetString("directory", directory);
            settings.SetString("format", format);
            settings.SetString("extension", "mp4");
            settings.SetBool("allow_spaces", false);
            _output.Update(settings);
            _replaySaved = onSaved;
            _replaySavePending = true;
            _replaySaveCompleted.Reset();
            if (_output.CallProcedure("save"))
                return true;

            _replaySaved = null;
            _replaySavePending = false;
            _replaySaveCompleted.Set();
            return false;
        }
    }

    private void OnReplaySaved(object? sender, EventArgs args)
    {
        try
        {
            Action<string>? callback;
            string? path;
            lock (_replayGate)
            {
                callback = _replaySaved;
                path = callback is null ? null : _output.CallStringProcedure("get_last_replay", "path");
                _replaySaved = null;
                _replaySavePending = false;
            }

            if (callback is not null && !string.IsNullOrWhiteSpace(path))
                callback(path);
        }
        finally
        {
            _replaySaveCompleted.Set();
        }
    }

    public bool WaitForReplaySave(TimeSpan timeout) => _replaySaveCompleted.Wait(timeout);

    // A quick clip whose save never signalled (the disk filled, or the muxer errored) left
    // _replaySaveCompleted reset forever, so an unbounded wait here hung every later shutdown.
    private static readonly TimeSpan ReplaySaveDisposeWait = TimeSpan.FromSeconds(30);

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_isReplayBuffer && !_replaySaveCompleted.Wait(ReplaySaveDisposeWait))
            Log.Warning("Recorder: a replay save was still pending when its output was disposed; the clip may be incomplete");
        if (_isReplayBuffer)
            _output.Saved -= OnReplaySaved;
        _output.Dispose();
        _ownedVideoEncoder?.Dispose();
        _audioEncoder?.Dispose();
        _audioRouting?.Dispose();
    }
}
