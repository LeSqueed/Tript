// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tript.Settings;

public enum HotkeyAction
{
    ToggleRecording,
    ManualBookmark,
    QuickClip,
}

public sealed class HotkeyBinding
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> UnknownProperties { get; set; } = new();

    // "Control" | "Shift" | "Alt" | "Win"
    public List<string> Modifiers { get; set; } = [];

    // A KeyboardEvent.code value (e.g. "KeyB", "F9") — the physical key, independent of layout/shift.
    // Null means the action is unbound.
    public string? Key { get; set; }
}

public sealed class HotkeySettings
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> UnknownProperties { get; set; } = new();

    public bool Enabled { get; set; } = true;

    public HotkeyBinding ToggleRecording { get; set; } = new() { Modifiers = ["Control", "Alt"], Key = "KeyR" };

    public HotkeyBinding ManualBookmark { get; set; } = new() { Modifiers = ["Control", "Alt"], Key = "KeyB" };

    public HotkeyBinding QuickClip { get; set; } = new() { Modifiers = ["Control", "Alt"], Key = "KeyC" };

    public int QuickClipSeconds { get; set; } = 30;
}
