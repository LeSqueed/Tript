// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Serilog;
using Tript.Obs;
using Tript.Settings;

namespace Tript.Recorder;

internal sealed class GameCaptureHookProbe : IDisposable
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan HookTimeout = TimeSpan.FromSeconds(30);

    private readonly ObsSource? _source;
    private readonly ObsSceneItem? _fallbackItem;
    private readonly CapturePolicy _policy;
    private readonly Lock _gate = new();
    private Timer? _timer;
    private int _ticks;
    private bool _hooked;
    private bool _timeoutReported;
    private bool _disposed;

    internal GameCaptureHookProbe(ObsSource? source, CapturePolicy policy, ObsSceneItem? fallbackItem = null)
    {
        _source = source;
        _policy = policy;
        _fallbackItem = fallbackItem;
    }

    internal bool IsHooked
    {
        get
        {
            lock (_gate)
            {
                return !_disposed && _source is { } capture && capture.Width > 0;
            }
        }
    }

    public void Dispose()
    {
        Stop();

        lock (_gate)
            _disposed = true;
    }

    internal void Start()
    {
        if (_source is null)
            return;

        lock (_gate)
        {
            if (_disposed || _timer is not null)
                return;

            _hooked = false;
            _ticks = 0;
            _timeoutReported = false;
            _timer = new Timer(Probe, null, ProbeInterval, ProbeInterval);
        }
    }

    internal void Stop()
    {
        Timer? probe;
        lock (_gate)
        {
            probe = _timer;
            _timer = null;
        }

        probe?.Dispose();
        SetFallbackVisible(true);
    }

    private void Probe(object? state)
    {
        lock (_gate)
        {
            if (_disposed || _timer is null)
                return;

            _ticks++;
            var hooked = _source is { Width: > 0 };

            if (hooked != _hooked)
            {
                _hooked = hooked;
                SetFallbackVisible(!hooked);
                if (hooked)
                {
                    Log.Information("ObsRecorderSession: game capture hooked at {Width}x{Height}",
                        _source!.Width, _source.Height);
                    if (_timeoutReported)
                        Log.Information("ObsRecorderSession: late game-capture warning cleared; hook recovered.");
                }
                else if (_policy.IncludesDisplayCapture)
                {
                    Log.Warning("ObsRecorderSession: game capture lost its hook; recording the display fallback.");
                }
                else
                {
                    Log.Warning("ObsRecorderSession: game capture lost its hook and there is no display layer.");
                }
            }

            var deadline = DeadlineFor(_policy);
            if (!hooked && !_timeoutReported && _ticks * ProbeInterval >= deadline)
            {
                _timeoutReported = true;
                Log.Warning("ObsRecorderSession: game capture has not hooked after {Seconds}s; " +
                            "continuing to retry while recording.", deadline.TotalSeconds);
            }
        }
    }

    private void SetFallbackVisible(bool visible)
    {
        if (_fallbackItem is null)
            return;

        try
        {
            _fallbackItem.SetVisible(visible);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal bool WaitForHook(TimeSpan deadline, TimeSpan warningAfter, Action showWarning,
        Action clearWarning, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(showWarning);
        ArgumentNullException.ThrowIfNull(clearWarning);

        if (_source is null)
            return true;

        var started = DateTime.UtcNow;
        var warningShown = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (IsHooked)
            {
                if (warningShown)
                    clearWarning();
                return true;
            }

            var elapsed = DateTime.UtcNow - started;
            if (warningAfter > TimeSpan.Zero && !warningShown && elapsed >= warningAfter)
            {
                warningShown = true;
                showWarning();
            }

            if (elapsed >= deadline)
                return false;

            lock (_gate)
            {
                if (_disposed)
                    return false;
            }

            if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(250)))
                break;
        }

        return false;
    }

    internal static TimeSpan DeadlineFor(CapturePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.Method == DisplayCaptureMethod.Game ? policy.GameCaptureTimeout : HookTimeout;
    }
}
