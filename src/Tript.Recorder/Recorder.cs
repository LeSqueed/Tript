// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Core;
using Tript.Obs;
using Tript.Settings;

namespace Tript.Recorder;

// The recorder state machine. Owns the IRecorderOutput a session builds (an ffmpeg_muxer output and
// its encoders for alpha), borrows the session's source, and is driven by the control plane — Start
// and Stop calls (which may come from the IPC surface or from the app host wiring the game
// detector).
//
// The contract this type gets right:
//
//   * It consumes an already-resolved ResolvedRecorderSettings, never the settings schema. The
//     settings layer computes the effective value; this type reads the flat config and nothing else.
//   * It is a state machine: Idle -> Recording -> Stopping -> Idle. A start from Recording or
//     Stopping is refused; a stop from Idle is a no-op.
//   * It surfaces why a recording stopped. The output's stop code is never swallowed — a start the
//     output refused, a failure it reported when it stopped, and an unsupported mode all become a
//     RecorderStopReason on the snapshot.
//
// Threading: Start and Stop are the control plane and may be called from any thread. Native stop
// completion is asynchronous, so output ownership remains deferred until its callback is quiescent.
public sealed class Recorder : IDisposable
{
    private readonly IRecorderSession _session;
    private readonly object _gate = new();

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

    // The current state and the reason the last recording ended. Safe to read from any thread;
    // the snapshot is immutable.
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

    // The output currently owned, or null. For the integration tests to assert on statistics.
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

    // Starts a session recording. Refused when already recording or stopping, and when the resolved
    // mode is not one the alpha recorder can run. Returns false without changing state in both
    // cases; the reason a refused start was refused is on the snapshot.
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

                // The stop signal is subscribed before Start, not at stop time: the output can end on
                // its own and that signal would be missed by a handler attached only when Stop is called.
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
                    // A native output can report an immediate stop while Start is returning. Do not
                    // place the source or publish Recording after that callback has completed.
                    if (!ReferenceEquals(_output, output) || _state != RecorderState.Idle)
                        throw new ObsException("The output stopped while it was starting.");

                    _sourcePlaced = true;
                    _session.PlaceSourceOnChannel();
                    _lastStopReason = null;
                    _lastStopCode = null;
                    _lastError = null;
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

    // Stops the recording in flight, if any. A no-op from Idle. The stop signal is what completes
    // the transition back to Idle; until then the recorder is in Stopping.
    public bool Stop()
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            if (_state == RecorderState.Idle)
                return false;

            var output = _output;
            if (output is null)
                return false;

            // The state moves BEFORE the call, never after. An output that raises its stop signal
            // synchronously inside Stop — the fake session, and libobs when the output ends inline —
            // re-enters this lock through RecordStop and completes the transition to Idle. Assigning
            // Stopping afterwards clobbered that back, leaving a recorder stuck in Stopping with a
            // null output: every later stop then dereferenced it, and the app's settle loop waited
            // out its full deadline on every single stop.
            _state = RecorderState.Stopping;
            if (output is IReplayBufferOutput replay)
                replay.WaitForReplaySave(Timeout.InfiniteTimeSpan);
            output.Stop();
            return true;
        }
    }

    // Stops the recording as a GameStopped end rather than a user request: the caller sets the stop
    // reason before the output's stop signal resolves it. No longer wired to the auto-start path,
    // but retained as the explicit GameStopped stop.
    internal bool StopForGameEnd()
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            if (_state == RecorderState.Idle)
                return false;

            var output = _output;
            if (output is null)
                return false;

            // Both the state and the reason go before the call, for the reason documented on Stop:
            // a synchronous stop signal resolves the reason as it passes through RecordStop.
            _state = RecorderState.Stopping;
            _lastStopReason = RecorderStopReason.GameStopped;
            if (output is IReplayBufferOutput replay)
                replay.WaitForReplaySave(Timeout.InfiniteTimeSpan);
            output.Stop();
            return true;
        }
    }

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
                // Set before the call, as in Stop: a synchronous stop signal reads the reason on its
                // way through RecordStop. The callback must complete before native ownership is
                // released.
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

            clearSource = _sourcePlaced;
            _sourcePlaced = false;

            // The callback, if any, has completed before this point. The live output and any output
            // deferred by that callback can now be released on the control-plane thread.
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

    // The output's stop signal. The handler may run on a libobs thread or be posted to the captured
    // synchronization context; it never releases the output inline.
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

            // The stop signal runs inside obs_output_stop on a libobs callback thread. Destroying
            // the output under those locks deadlocks, so the destructor is deferred to a slot the
            // recorder's own control plane drains (next Start, or Dispose) — never the callback.
            _output = null;
            _pendingDispose = output;
            _state = RecorderState.Idle;

            // The stop code is never swallowed. The only clean end is the one this recorder asked
            // for; a code on a recording that expected to keep going is a failure to surface.
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

            // The source was borrowed; it goes back to the app. The channel is cleared so nothing
            // keeps rendering a source that is no longer being recorded.
            if (_sourcePlaced)
            {
                _sourcePlaced = false;
                _session.ClearSourceFromChannel();
            }
        }
    }

    // Disposes an output that a stop callback deferred, on the caller's thread. Called holding the
    // gate, from a control-plane entry point (Start), so a libobs callback thread is never inside.
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
