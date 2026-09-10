// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tript.Core;

namespace Tript.Settings;

public sealed class GameSetting
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> UnknownProperties { get; set; } = new();

    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Executable { get; set; }

    public string? ExecutablePath { get; set; }

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

    public bool? AutoRecordOverride { get; set; }

    public GameRecordingModeOverride? RecordingModeOverride { get; set; }

    public GameQualityOverride? QualityOverride { get; set; }

    public GameCaptureMethodOverride? CaptureMethodOverride { get; set; }

    public GameAutomaticClipOverride? AutomaticClipOverride { get; set; }
}

public sealed class GameCaptureMethodOverride
{
    public DisplayCaptureMethod Method { get; set; }
}

public sealed class GameRecordingModeOverride
{
    public RecordingMode Mode { get; set; }
}

public sealed class GameQualityOverride
{
    public int? ResolutionWidth { get; set; }

    public int? ResolutionHeight { get; set; }

    public int? Fps { get; set; }

    public string? Encoder { get; set; }

    public int? Quality { get; set; }
}

public sealed class GameAutomaticClipOverride
{
    public int? BeforeSeconds { get; set; }

    public int? AfterSeconds { get; set; }
}
