// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

// The settings model and its persistence, structured around the six logical pages the settings
// UI is split into: general, recording, buffer/replay, audio, capture and game.
// The shape — a typed object with typed page sub-objects, unknown keys preserved on round-trip —
// is what lets a new property slot into the page it belongs to rather than growing a parallel
// structure.

// The file contract: JSON, versioned, at the platform config directory under the product name
// (Tript, not ReferenceProduct — the directories rename with the project and need a migration path; see
// charter/branding.md). Unknown keys survive a load because each page keeps the original
// dictionary alongside its typed properties, so an older build writing a field a newer build has
// not modelled yet is not lost, and a newer build writing a field an older build has not
// modelled yet is not rejected.

// The resolver is a distinct component (SettingsResolver): the recorder receives already-resolved
// values and never depends on this schema or owns the merge. That is the seam the dependency map
// flagged as the source of the reference's cycles, so it is drawn here rather than left to the
// recorder.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tript.Settings;

// A group of settings the settings UI loads and saves as one unit.
public enum SettingsPage
{
    Recording,
    Buffer,
    Audio,
    Capture,
    Game,
    General
}

// The top-level settings object: six page objects plus the per-game override collection. The
// three game-side types (quality override, recording-mode override, integration settings) live
// under each GameSetting, because a game's settings address the game, not a page.
public sealed class Settings
{
    public const int CurrentVersion = 1;

    // JsonExtensionData is the forward-compatibility mechanism: a property this build does not
    // model lands here and is re-emitted on save, so an older build writing a field a newer
    // build has not modelled yet is not lost. It also tolerates duplicate keys (last wins)
    // rather than failing the load.
    [JsonExtensionData]
    public Dictionary<string, JsonElement> UnknownProperties { get; set; } = new();

    public int Version { get; set; } = CurrentVersion;

    // A free-form note for the user; persisted like any other field, though Tript itself neither
    // writes nor reads it.
    public string? Notes { get; set; }

    public RecordingSettings Recording { get; set; } = new();

    public BufferSettings Buffer { get; set; } = new();

    public AudioSettings Audio { get; set; } = new();

    public CaptureSettings Capture { get; set; } = new();

    public GameSettings Game { get; set; } = new();

    public GeneralSettings General { get; set; } = new();
}
