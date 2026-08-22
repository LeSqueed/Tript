// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

// Serialization helpers for the settings model and the recording metadata. One JsonSerializerOptions
// instance is shared across the whole surface so enums, converters and unknown-key handling behave
// identically everywhere.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tript.Settings;

public static class SettingsSerialization
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    // A dedicated converter reads the content-type vocabulary; the rest of the enum surface is
    // written by name (JsonStringEnumConverter) so the file stays readable and the names are the
    // compatibility surface, not the ordinals. The read side is deliberately lenient while the
    // write side stays exact.
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

        // The specific converter must precede the tolerant generic enum converter: options.Converters is
        // walked in order and the first converter that can handle the type wins, so a generic
        // string-enum converter registered first would swallow ContentType and its unknown-name
        // tolerance would never run.
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

        return settings;
    }

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
