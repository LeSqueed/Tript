// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;

namespace Tript.App;

internal sealed record DesktopHotkeyTriggers(string? ToggleRecording, string? ManualBookmark, string? QuickClip)
{
    internal static DesktopHotkeyTriggers None { get; } = new(null, null, null);

    internal static DesktopHotkeyTriggers From(IReadOnlyDictionary<HotkeyAction, string> triggers) => new(
        triggers.GetValueOrDefault(HotkeyAction.ToggleRecording),
        triggers.GetValueOrDefault(HotkeyAction.ManualBookmark),
        triggers.GetValueOrDefault(HotkeyAction.QuickClip));
}

internal sealed record PlatformCapabilities(
    string Platform,
    bool Tray,
    bool StartWithSystem,
    bool HideToTray,
    bool ObsSharing,
    bool GlobalHotkeys,
    string? GlobalHotkeysNote,
    bool Notifications,
    bool NotificationSounds,
    bool GlobalHotkeysManagedByDesktop = false,
    bool GlobalHotkeysConfigurable = false,
    DesktopHotkeyTriggers? DesktopHotkeyTriggers = null,
    bool ScreenChosenByDesktop = false,
    bool ScreenChoiceRemembered = false)
{
    internal static PlatformCapabilities ForCurrentOs() =>
        OperatingSystem.IsWindows() ? Windows : Unix(OperatingSystem.IsMacOS() ? "macos" : "linux");

    internal static PlatformCapabilities Windows { get; } = new(
        Platform: "windows",
        Tray: true,
        StartWithSystem: true,
        HideToTray: true,
        ObsSharing: true,
        GlobalHotkeys: true,
        GlobalHotkeysNote: null,
        Notifications: true,
        NotificationSounds: true);

    internal static PlatformCapabilities Unix(string platform) => new(
        Platform: platform,
        Tray: false,
        StartWithSystem: false,
        HideToTray: false,
        ObsSharing: false,
        GlobalHotkeys: false,
        GlobalHotkeysNote: null,
        Notifications: false,
        NotificationSounds: false);
}
