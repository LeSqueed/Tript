// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tript.Settings;

public enum SettingsPage
{
    Recording,
    Buffer,
    Audio,
    Capture,
    Game,
    General,
    Hotkeys
}

public sealed class Settings
{
    public const int CurrentVersion = 1;

    [JsonExtensionData]
    public Dictionary<string, JsonElement> UnknownProperties { get; set; } = new();

    public int Version { get; set; } = CurrentVersion;

    public string? Notes { get; set; }

    public RecordingSettings Recording { get; set; } = new();

    public BufferSettings Buffer { get; set; } = new();

    public AudioSettings Audio { get; set; } = new();

    public CaptureSettings Capture { get; set; } = new();

    public GameSettings Game { get; set; } = new();

    public GeneralSettings General { get; set; } = new();

    public HotkeySettings Hotkeys { get; set; } = new();
}
