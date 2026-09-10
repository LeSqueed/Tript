// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tript.Settings;

public static class SettingsSerialization
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };

        options.Converters.Add(new ContentTypeConverter());
        options.Converters.Add(new TolerantDateTimeConverter());
        options.Converters.Add(new TolerantEnumConverterFactory());
        return options;
    }

    public static string Serialize(Settings settings) => JsonSerializer.Serialize(settings, Options);

    public static Settings? Deserialize(string json)
    {
        var settings = JsonSerializer.Deserialize<Settings>(json, Options);
        if (settings is null)
            return null;

        RemoveRemovedGeneralProperties(settings.General);
        NormalizeRecordingModes(settings);

        return settings;
    }

    private static void NormalizeRecordingModes(Settings settings)
    {
        settings.Recording.Mode = NormalizeRecordingMode(settings.Recording.Mode);
        foreach (var game in settings.Game.GameList)
        {
            if (game.RecordingModeOverride is not null)
                game.RecordingModeOverride.Mode = NormalizeRecordingMode(game.RecordingModeOverride.Mode);
        }
    }

    private static RecordingMode NormalizeRecordingMode(RecordingMode mode) => mode switch
    {
        RecordingMode.Buffer or RecordingMode.Hybrid => RecordingMode.SessionWithReplayBuffer,
        _ => mode,
    };

    public static void RemoveRemovedGeneralProperties(GeneralSettings general)
    {
        foreach (var key in general.UnknownProperties.Keys
                     .Where(key => key.Equals("startupWindow", StringComparison.OrdinalIgnoreCase)
                                || key.Equals("closeButtonAction", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            general.UnknownProperties.Remove(key);
        }
    }
}
