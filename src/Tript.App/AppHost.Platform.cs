// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Serilog;
using Tript.Settings;

namespace Tript.App;

internal sealed partial class AppHost
{
    private readonly Lock _capabilitiesGate = new();
    private PlatformCapabilities _capabilities = PlatformCapabilities.ForCurrentOs();

    internal PlatformCapabilities Capabilities
    {
        get
        {
            lock (_capabilitiesGate)
                return _capabilities;
        }
    }

    internal Func<bool>? GlobalHotkeyConfigurator { get; set; }

    internal void ReportGlobalHotkeys(bool available, string? note, bool managedByDesktop = false,
        bool configurable = false) =>
        ChangeCapabilities(current => current with
        {
            GlobalHotkeys = available,
            GlobalHotkeysNote = note,
            GlobalHotkeysManagedByDesktop = managedByDesktop,
            GlobalHotkeysConfigurable = configurable,
        });

    internal void ReportDesktopHotkeyTriggers(IReadOnlyDictionary<HotkeyAction, string> triggers) =>
        ChangeCapabilities(current => current with { DesktopHotkeyTriggers = DesktopHotkeyTriggers.From(triggers) });

    internal void ConfigureGlobalHotkeys()
    {
        var configurator = GlobalHotkeyConfigurator;
        if (configurator is null || !Capabilities.GlobalHotkeysConfigurable)
            return;

        bool opened;
        try
        {
            opened = configurator();
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "AppHost: the desktop's shortcut settings could not be opened");
            opened = false;
        }

        if (!opened)
            PushError("Your desktop's shortcut settings could not be opened. Change Tript's shortcuts in the " +
                      "desktop's keyboard shortcut settings instead.");
    }

    internal void ReportNotifications(bool notifications, bool sounds) =>
        ChangeCapabilities(current => current with { Notifications = notifications, NotificationSounds = sounds });

    private void ChangeCapabilities(Func<PlatformCapabilities, PlatformCapabilities> change)
    {
        bool changed;
        lock (_capabilitiesGate)
        {
            var next = change(_capabilities);
            changed = next != _capabilities;
            _capabilities = next;
        }

        if (changed && !_disposed)
            PushSettings();
    }
}
