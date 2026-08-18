// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tript.Settings;

// A known/detected game and its per-game configuration. Per-game quality and recording-mode
// overrides are distinct types: one affects the encoder's quality
// profile, the other which recording mode runs for this game.
public sealed class GameSetting
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> UnknownProperties { get; set; } = new();

    public string Id { get; set; } = string.Empty;

    // The display name. Shown in the UI and written into a recording's metadata record; it has no
    // bearing on what is detected or hooked.
    public string Name { get; set; } = string.Empty;

    // The process/executable this game runs as, with or without a `.exe` — auto-detection matches it
    // against the running process list and game capture hooks it. Optional: absent (the shape every
    // settings file written before this field has) means the display Name doubles as the executable,
    // which is exactly how it behaved when Name did both jobs.
    public string? Executable { get; set; }

    // Never written to the settings file: this is Executable-or-Name, not a stored value. Callers
    // that need to compare it against a running process name normalize it first
    // (ProcessNameGameDetector.NormalizeProcessName) — the `.exe` is optional on both sides.
    [JsonIgnore]
    public string EffectiveExecutable
        => string.IsNullOrWhiteSpace(Executable) ? Name : Executable.Trim();

    public string? IconId { get; set; }

    // A per-game override of the global recording-mode setting.
    public GameRecordingModeOverride? RecordingModeOverride { get; set; }

    // A per-game override of the global quality settings (resolution, fps, encoder, quality).
    public GameQualityOverride? QualityOverride { get; set; }

    // Per-game telemetry integration toggles. Game telemetry is externally dictated — ports,
    // config formats — and each integration is gated by a per-game Enabled toggle.
    public GameIntegrationSettings Integrations { get; set; } = new();
}

// Per-game recording mode: null means "inherit the global setting".
public sealed class GameRecordingModeOverride
{
    public RecordingMode Mode { get; set; }
}

// Per-game quality override. Each field is nullable so a game can override only the aspect that
// matters to it; a null field inherits the global value.
public sealed class GameQualityOverride
{
    public int? ResolutionWidth { get; set; }

    public int? ResolutionHeight { get; set; }

    public int? Fps { get; set; }

    public string? Encoder { get; set; }

    public int? Quality { get; set; }
}

// Per-game telemetry integration toggles. The settings model only records which are enabled.
public sealed class GameIntegrationSettings
{
    public bool Enabled { get; set; }
}
