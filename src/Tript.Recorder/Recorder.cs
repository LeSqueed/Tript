// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Core;
using Tript.Obs;
using Tript.Settings;

namespace Tript.Recorder;

public sealed class Recorder : IDisposable
{
    private readonly IRecorderSession _session;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _idle = new(initialState: true);

    private IRecorderOutput? _output;
    private IRecorderOutput? _pendingDispose;
    private EventHandler<ObsOutputStopEvent>? _stopHandler;
    private ResolvedRecorderSettings _settings;
    private RecorderState _state = RecorderState.Idle;
    private RecorderStopReason? _lastStopReason;
    private ObsOutputStopCode? _lastStopCode;
    private string? _lastError;
    private bool _disposed;
    private bool _disposing;
    private bool _sourcePlaced;

    public Recorder(IRecorderSession session, ResolvedRecorderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(settings.AudioTracks);

        _session = session;
        _settings = settings.Clone();
    }

    public RecorderStateSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new RecorderStateSnapshot(_state, _lastStopReason, _lastStopCode, _lastError);
            }
        }
    }

    internal IRecorderOutput? Output
    {
        get
        {
            lock (_gate)
            {
                return _output;
            }
        }
    }

    public bool Start(ResolvedRecorderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(settings.AudioTracks);

        IRecorderOutput? cleanupOutput = null;
        var cleanupOutputStop = false;
        var clearSource = false;
        var started = false;

        lock (_gate)
        {
            ThrowIfDisposed();

            if (_disposing)
                return false;

            if (_state != RecorderState.Idle)
                return false;

            DrainPendingDisposeLocked();

            if (!settings.Mode.IsAlphaSupported())
            {
                _lastStopReason = RecorderStopReason.UnsupportedMode;
                _lastStopCode = null;
                _lastError = $"Recording mode '{settings.Mode}' is not supported.";
                return false;
            }

            _settings = settings.Clone();

            IRecorderOutput? output = null;
            EventHandler<ObsOutputStopEvent>? handler = null;
            try
            {
                output = _session.CreateOutput(_settings);
                _output = output;

                handler = new EventHandler<ObsOutputStopEvent>(OnStopped);
                _stopHandler = handler;
                output.Stopped += handler;

                if (!output.Start())
                {
                    output.Stopped -= handler;
                    _stopHandler = null;
                    _output = null;
                    cleanupOutput = output;
                    _lastStopReason = RecorderStopReason.StartRefused;
                    _lastStopCode = null;
                    _lastError = string.IsNullOrEmpty(output.LastError) ? null : output.LastError;
                }
                else
                {
                    if (!ReferenceEquals(_output, output) || _state != RecorderState.Idle)
                        throw new ObsException("The output stopped while it was starting.");

                    _sourcePlaced = true;
                    _session.PlaceSourceOnChannel();
                    _lastStopReason = null;
                    _lastStopCode = null;
                    _lastError = null;
                    _idle.Reset();
                    _state = RecorderState.Recording;
                    started = true;
                }
            }
            catch (Exception exception)
            {
                if (handler is not null && ReferenceEquals(_stopHandler, handler))
                {
                    output!.Stopped -= handler;
                    _stopHandler = null;
                }

                if (_sourcePlaced)
                {
                    _sourcePlaced = false;
                    clearSource = true;
                }

                if (output is not null)
                {
                    if (ReferenceEquals(_output, output))
                        _output = null;
                    if (ReferenceEquals(_pendingDispose, output))
                        _pendingDispose = null;
                    cleanupOutput = output;
                    cleanupOutputStop = true;
                }

                _lastStopReason = RecorderStopReason.StartRefused;
                _lastStopCode = null;
                _lastError = exception.Message;
            }
        }

        if (clearSource)
            _session.ClearSourceFromChannel();

        if (cleanupOutput is not null)
        {
            if (cleanupOutputStop)
            {
                if (!cleanupOutput.WaitForStop(TimeSpan.Zero))
                    cleanupOutput.Stop();
                cleanupOutput.WaitForStop(Timeout.InfiniteTimeSpan);
            }

            cleanupOutput.Dispose();
        }

        return started;
    }

    public bool SaveReplayBuffer(string directory, string format, Action<string> onSaved)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentException.ThrowIfNullOrEmpty(format);
        ArgumentNullException.ThrowIfNull(onSaved);

        lock (_gate)
        {
            if (_state != RecorderState.Recording || _output is not IReplayBufferOutput replay)
                return false;
            return replay.SaveReplay(directory, format, onSaved);
        }
    }

    public bool Stop() => Stop(null);

    public bool WaitForIdle(TimeSpan timeout) => _idle.Wait(timeout);

    private bool Stop(RecorderStopReason? reason)
    {
        IRecorderOutput output;
        lock (_gate)
        {
            ThrowIfDisposed();

            // Recording is the only state Stop() may act from — a second call arriving while the
            // first stop is still in flight (state already Stopping) must be a no-op. Forwarding to
            // output.Stop() twice tells libobs to stop an output it is already tearing down, which
            // crashes the process (STATUS_ACCESS_VIOLATION inside obs.dll) rather than throwing a
            // catchable .NET exception.
            if (_state != RecorderState.Recording || _output is null)
                return false;

            output = _output;
            _state = RecorderState.Stopping;
            if (reason is not null)
                _lastStopReason = reason;
        }

        try
        {
            if (output is IReplayBufferOutput replay)
                replay.WaitForReplaySave(Timeout.InfiniteTimeSpan);
            output.Stop();
        }
        catch (ObjectDisposedException)
        {
        }

        return true;
    }

    internal bool StopForGameEnd() => Stop(RecorderStopReason.GameStopped);

    public void Dispose()
    {
        IRecorderOutput? stopping;
        lock (_gate)
        {
            if (_disposed || _disposing)
                return;

            _disposing = true;

            if (_state != RecorderState.Idle)
            {
                _lastStopReason = RecorderStopReason.Disposed;
                _state = RecorderState.Stopping;
                stopping = _output;
            }
            else
            {
                stopping = null;
            }
        }

        if (stopping is not null)
        {
            try
            {
                if (stopping is IReplayBufferOutput replay)
                    replay.WaitForReplaySave(Timeout.InfiniteTimeSpan);
                stopping.Stop();
                stopping.WaitForStop(Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        IRecorderOutput? toDispose;
        var clearSource = false;
        lock (_gate)
        {
            _disposed = true;
            _disposing = false;
            _idle.Set();

            clearSource = _sourcePlaced;
            _sourcePlaced = false;

            toDispose = _pendingDispose ?? _output;
            _pendingDispose = null;
            _output = null;
        }

        if (clearSource)
            _session.ClearSourceFromChannel();

        toDispose?.Dispose();
    }

    internal void DrainCompletedOutput()
    {
        IRecorderOutput? toDispose;
        lock (_gate)
        {
            if (_state != RecorderState.Idle)
                return;

            toDispose = _pendingDispose;
            _pendingDispose = null;
        }

        if (toDispose is not null)
        {
            if (toDispose is IReplayBufferOutput replay)
                replay.WaitForReplaySave(Timeout.InfiniteTimeSpan);
            toDispose.WaitForStop(Timeout.InfiniteTimeSpan);
            toDispose.Dispose();
        }
    }

    private void OnStopped(object? sender, ObsOutputStopEvent stop) => RecordStop(stop);

    private void RecordStop(ObsOutputStopEvent stop)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            var output = _output;
            if (output is null)
                return;

            _output = null;
            _pendingDispose = output;
            _state = RecorderState.Idle;
            _idle.Set();

            _lastStopCode = stop.Code;
            _lastError = stop.LastError;
            _lastStopReason = stop.Code == ObsOutputStopCode.Success
                ? _lastStopReason ?? RecorderStopReason.UserRequested
                : RecorderStopReason.OutputFailure;

            if (_stopHandler is not null)
            {
                output.Stopped -= _stopHandler;
                _stopHandler = null;
            }

            if (_sourcePlaced)
            {
                _sourcePlaced = false;
                _session.ClearSourceFromChannel();
            }
        }
    }

    private void DrainPendingDisposeLocked()
    {
        if (_pendingDispose is null)
            return;

        var output = _pendingDispose;
        _pendingDispose = null;
        output.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
