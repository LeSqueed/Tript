// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Serilog;
using Tript.App;

namespace Tript.Shell.Linux;

internal sealed class LinuxShellNotifications : IDisposable
{
    private readonly AppHost _host;
    private readonly string? _iconPath;
    private volatile LinuxNotifications? _notifications;
    private volatile LinuxSoundPlayer? _sounds;
    private volatile bool _windowFocused;
    private volatile bool _disposed;

    internal LinuxShellNotifications(AppHost host)
    {
        _host = host;
        var icon = Path.GetFullPath(Path.Combine(host.Options.WebRoot, "tript.png"));
        _iconPath = File.Exists(icon) ? icon : null;
        ThreadPool.QueueUserWorkItem(_ => Start());
    }

    internal void SetWindowFocused(bool focused) => _windowFocused = focused;

    internal void Show(NotificationKind kind, string title, string body)
    {
        var notifications = _notifications;
        var sounds = _sounds;
        var plan = ShellNotifications.Plan(_host.SettingsStore.Load().General.Notifications, kind, _windowFocused,
            canNotify: notifications is not null, canPlaySound: sounds?.Available == true);

        try
        {
            if (plan.Notify)
                notifications!.Show(title, body, _iconPath);
            if (plan.PlaySound && NotificationSoundFiles.PathFor(kind, _host.Options.WebRoot) is { } sound)
                sounds!.Play(sound);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Tript.Shell: could not send notification");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _notifications?.Dispose();
    }

    private void Start()
    {
        var sounds = LinuxSoundPlayer.ForThisSystem();
        var notifications = LinuxNotifications.TryOpen();
        if (_disposed)
        {
            notifications?.Dispose();
            return;
        }

        _sounds = sounds;
        _notifications = notifications;
        _host.ReportNotifications(notifications is not null, sounds.Available);
    }
}
