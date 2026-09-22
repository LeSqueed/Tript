// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.App.Updater;

namespace Tript.App;

internal sealed partial class AppHost
{
    internal Task CheckForUpdatesManualAsync() =>
        _updateManager is null ? Task.CompletedTask : _updateManager.CheckAsync(manual: true, CancellationToken.None);

    // The setting used to be read only once, at startup, while the 24 hour timer was armed
    // unconditionally and never looked at it, so turning automatic updates off still downloaded an
    // update the next day. Every automatic check now asks, so the toggle takes effect immediately.
    internal Task CheckForUpdatesAutomaticAsync() =>
        _updateManager is null || !AutomaticUpdateChecksEnabled
            ? Task.CompletedTask
            : _updateManager.CheckAsync(manual: false, CancellationToken.None);

    internal bool AutomaticUpdateChecksEnabled => _settingsStore.Load().General.CheckForUpdatesAutomatically;

    internal void ApplyUpdate()
    {
        if (_updateManager is null || !_updateManager.TryApply())
            return;

        RestartForUpdateRequested?.Invoke();
    }

    internal void PushUpdateStatus()
    {
        if (_updateManager is null)
            return;

        _ipc.Broadcast("updateProgress", _updateManager.Snapshot());
    }

    // Only real stage transitions notify; snapshot pushes on reconnect must not re-toast.
    private void OnUpdateStatusChanged(UpdateStatusPayload status)
    {
        PushUpdateStatus();

        if (status.Stage is "ready" or "available" && _lastNotifiedUpdateStage != status.Stage)
        {
            var body = status.Stage == "ready"
                ? $"Tript {status.Version} is ready to install. Restart to update."
                : $"Tript {status.Version} is available.";
            RequestNotification(NotificationKind.UpdateReady, "Update ready", body);
        }

        _lastNotifiedUpdateStage = status.Stage;
    }
}
