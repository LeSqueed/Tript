// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Photino.NET;
using Serilog;
using Tript.App;
using Tript.Settings;

namespace Tript.Shell;

internal readonly record struct NotificationPlan(bool Notify, bool PlaySound);

internal static class ShellNotifications
{
    internal static void Show(PhotinoWindow? window, Func<Action, bool> invoke, AppHost host, NotificationKind kind,
        string title, string body)
    {
        if (!OperatingSystem.IsWindows() || window is null)
            return;

        var notifications = host.SettingsStore.Load().General.Notifications;
        if (!notifications.Enabled)
            return;

        var showToast = NotificationEnabled(notifications, kind);
        var playSound = SoundEnabled(notifications, kind);
        if (!showToast && !playSound)
            return;

        try
        {
            var shown = invoke(() =>
            {
                if (WindowsWindow.IsForeground(window))
                    return;
#if WINDOWS_TOAST
                if (showToast)
                    WindowsToastNotifications.Show(title, body, Path.Combine(host.Options.WebRoot, "tript.png"));
                if (playSound)
                    NativeSound.Play(kind, host.Options.WebRoot);
#else
                if (showToast)
                    window.SendNotification(title, body);
#endif
            });
            if (!shown)
                Log.Debug("Tript.Shell: dropped the {Kind} notification because the window is not ready", kind);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Tript.Shell: could not send notification");
        }
    }

    internal static NotificationPlan Plan(NotificationSettings settings, NotificationKind kind, bool windowFocused,
        bool canNotify, bool canPlaySound)
    {
        if (!settings.Enabled || windowFocused)
            return new NotificationPlan(false, false);

        return new NotificationPlan(canNotify && NotificationEnabled(settings, kind),
            canPlaySound && SoundEnabled(settings, kind));
    }

    internal static bool NotificationEnabled(NotificationSettings settings, NotificationKind kind) => kind switch
    {
        NotificationKind.RecordingStarted => settings.RecordingStarted,
        NotificationKind.RecordingStopped => settings.RecordingStopped,
        NotificationKind.Error => settings.Errors,
        NotificationKind.UpdateReady => true,
        _ => false,
    };

    internal static bool SoundEnabled(NotificationSettings settings, NotificationKind kind) => kind switch
    {
        NotificationKind.RecordingStarted => settings.RecordingStartedSound,
        NotificationKind.RecordingStopped => settings.RecordingStoppedSound,
        NotificationKind.Error => settings.ErrorsSound,
        NotificationKind.UpdateReady => false,
        _ => false,
    };
}
