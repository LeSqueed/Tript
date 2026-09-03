// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tript.Core;

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

    // The exact executable path a custom game runs from, e.g. "C:\Games\Example\game.exe". Custom
    // games match only this path, so an unrelated process that shares the file name can never be
    // mistaken for the game. Packaged games never carry this: their installations come from the
    // launcher inventory or their basename.
    public string? ExecutablePath { get; set; }

    // Never written to the settings file. Callers comparing it against a running process name go
    // through ExecutableNames.Normalize, so the `.exe` is optional on both sides.
    [JsonIgnore]
    public string EffectiveExecutable
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ExecutablePath))
                return FilePaths.FileName(ExecutablePath.Trim());
            return string.IsNullOrWhiteSpace(Executable) ? Name : Executable.Trim();
        }
    }

    public string? IconId { get; set; }

    public GameRecordingModeOverride? RecordingModeOverride { get; set; }

    public GameQualityOverride? QualityOverride { get; set; }

    // Per-game capture method: null means "inherit the global capture setting". A title that will
    // not hook wants the display layer even when the global asks for game capture only, and a title
    // whose window must never leak the desktop wants the opposite.
    public GameCaptureMethodOverride? CaptureMethodOverride { get; set; }

    public GameAutomaticClipOverride? AutomaticClipOverride { get; set; }

    // Per-game telemetry integration toggles. Game telemetry is externally dictated — ports,
    // config formats — and each integration is gated by a per-game Enabled toggle.
    public GameIntegrationSettings Integrations { get; set; } = new();
}

public sealed class GameCaptureMethodOverride
{
    public DisplayCaptureMethod Method { get; set; }
}

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

// Per-game automatic-clip override. Each field is nullable so a game can override only the side it
// cares about; a null field inherits the global value.
public sealed class GameAutomaticClipOverride
{
    public int? BeforeSeconds { get; set; }

    public int? AfterSeconds { get; set; }
}

public sealed class GameIntegrationSettings
{
    public bool Enabled { get; set; }
}
