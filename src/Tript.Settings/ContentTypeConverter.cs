// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tript.Settings;

public sealed class ContentTypeConverter : JsonConverter<ContentType>
{
    public override bool HandleNull => true;

    public override ContentType Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && Enum.TryParse<ContentType>(reader.GetString(), ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed))
        {
            return parsed;
        }

        reader.Skip();
        return ContentType.Recording;
    }

    public override void Write(Utf8JsonWriter writer, ContentType value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
