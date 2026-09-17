// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;

namespace Tript.Recorder;

internal sealed class CombinedRecorderOutput : IRecorderOutput, IReplayBufferOutput
{
    private readonly MuxerOutput _session;
    private readonly MuxerOutput _replay;
    private readonly object _gate = new();
    private bool _sessionStopped;
    private bool _replayStopped;
    private bool _signalled;

    internal CombinedRecorderOutput(MuxerOutput session, MuxerOutput replay)
    {
        _session = session;
        _replay = replay;
        _session.Stopped += OnSessionStopped;
        _replay.Stopped += OnReplayStopped;
    }

    public bool IsActive => _session.IsActive || _replay.IsActive;

    public string? LastError => _session.LastError ?? _replay.LastError;

    public event EventHandler<ObsOutputStopEvent>? Stopped;

    public bool Start()
    {
        lock (_gate)
        {
            _sessionStopped = false;
            _replayStopped = false;
            _signalled = false;
        }

        if (!_session.Start())
            return false;
        if (_replay.Start())
            return true;

        _session.Stop();
        return false;
    }

    public void Stop()
    {
        if (_replay.IsActive)
            _replay.Stop();
        if (_session.IsActive)
            _session.Stop();
    }

    public bool WaitForStop(TimeSpan timeout) =>
        _session.WaitForStop(timeout) && _replay.WaitForStop(timeout);

    public bool SaveReplay(string directory, string format, Action<string> onSaved) =>
        _replay.SaveReplay(directory, format, onSaved);

    public bool WaitForReplaySave(TimeSpan timeout) => _replay.WaitForReplaySave(timeout);

    private void OnSessionStopped(object? sender, ObsOutputStopEvent stop)
    {
        lock (_gate)
            _sessionStopped = true;
        TrySignal(stop);
    }

    private void OnReplayStopped(object? sender, ObsOutputStopEvent stop)
    {
        lock (_gate)
            _replayStopped = true;
        TrySignal(stop);
    }

    private void TrySignal(ObsOutputStopEvent stop)
    {
        lock (_gate)
        {
            if (_signalled || !_sessionStopped || !_replayStopped)
                return;
            _signalled = true;
        }

        Stopped?.Invoke(this, stop);
    }

    public void Dispose()
    {
        _session.Stopped -= OnSessionStopped;
        _replay.Stopped -= OnReplayStopped;
        _replay.Dispose();
        _session.Dispose();
    }
}
