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
    // compatibility surface, not the ordinals.
    //
    // The read side is deliberately lenient while the write side stays exact. These files are read
    // back by builds that did not write them and they are hand-edited (a recovered recording gets
    // its game typed in), so a record that is nearly right must load rather than count as garbage:
    // a record that fails to parse is a record whose game, title and bookmarks are at risk. Each
    // flag below was picked against a measured failure of the previous options:
    //
    //   PropertyNameCaseInsensitive  A record with PascalCase members ("VideoPath") parsed into an
    //                                empty record — no exception, silently no videoPath and no game.
    //   AllowTrailingCommas          {"game":"Overwatch",} threw "The JSON object contains a trailing
    //                                comma at the end which is not supported in this mode."
    //   ReadCommentHandling.Skip     A // comment threw "'/' is invalid after a value."
    //   AllowReadingFromString       "durationSeconds": "9.13" threw "The JSON value could not be
    //                                converted to System.Nullable`1[System.Double]".
    //
    // None of them changes what is written: the serialized form is still camelCase, indented, with
    // numbers written as numbers.
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

        // The specific converter must precede the generic enum converter: options.Converters is
        // walked in order and the first converter that can handle the type wins, so a generic
        // string-enum converter registered first would swallow ContentType and its unknown-name
        // tolerance would never run.
        options.Converters.Add(new ContentTypeConverter());
        options.Converters.Add(new TolerantDateTimeConverter());
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static string Serialize(Settings settings) => JsonSerializer.Serialize(settings, Options);

    public static Settings? Deserialize(string json) => JsonSerializer.Deserialize<Settings>(json, Options);
}
