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

    public string Name { get; set; } = string.Empty;

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
