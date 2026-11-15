// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Serilog;

namespace Tript.App;

internal sealed partial class AppHost
{
    internal static readonly TimeSpan DefaultRecoveryRestartMinimumUptime = TimeSpan.FromMinutes(2);

    private readonly long _startedAtTicks = Environment.TickCount64;
    private GraphicsDeviceMonitor? _graphicsDevice;
    private int _recoveryRestartRequested;

    internal event Action? RestartRequested;

    // A process that needs recovery again right after starting would restart in a loop.
    internal TimeSpan RecoveryRestartMinimumUptime { get; set; } = DefaultRecoveryRestartMinimumUptime;

    internal bool GraphicsDeviceLost => _graphicsDevice?.IsLost == true;

    private void WatchGraphicsDevice(GraphicsDeviceMonitor monitor)
    {
        _graphicsDevice = monitor;
        monitor.Lost += OnGraphicsDeviceLost;
        if (monitor.IsLost)
            OnGraphicsDeviceLost();
    }

    private void StopWatchingGraphicsDevice()
    {
        if (_graphicsDevice is not null)
            _graphicsDevice.Lost -= OnGraphicsDeviceLost;
    }

    private void OnGraphicsDeviceLost() =>
        RunGuarded(RecoverFromGraphicsDeviceLoss, "recovering from a lost graphics device");

    private void RecoverFromGraphicsDeviceLoss()
    {
        if (_disposed)
            return;

        Log.Error("AppHost: the graphics device was lost (the GPU driver reset or crashed); "
            + "libobs cannot rebuild it without a display, so nothing more can be recorded in this process");

        StopRecording();
        RequestRecoveryRestart("the graphics driver reset",
            "Recording stopped because the graphics driver reset. Restart Tript to record again.");
    }

    private void RequestRecoveryRestart(string reason, string messageWhenNotRestarting)
    {
        bool shuttingDown;
        lock (_recorderGate)
            shuttingDown = _shuttingDown || _disposed;
        if (shuttingDown)
            return;

        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64 - _startedAtTicks);
        var restart = RestartRequested;
        if (restart is null || uptime < RecoveryRestartMinimumUptime)
        {
            Log.Error("AppHost: not restarting automatically after {Reason} ({Why})", reason,
                restart is null ? "no shell to restart" : "it already happened shortly after start");
            PushError(messageWhenNotRestarting);
            return;
        }

        if (Interlocked.Exchange(ref _recoveryRestartRequested, 1) != 0)
            return;

        Log.Warning("AppHost: restarting Tript to recover from {Reason}", reason);
        RequestNotification(NotificationKind.Error, "Tript is restarting",
            $"Tript is restarting to recover from {reason}.");
        restart();
    }
}
