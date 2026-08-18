// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

// Reads and writes the content-type vocabulary. The serialised form is the member name; the
// converter's job is graceful degradation, exactly like BookmarkTypeConverter in Tript.Core: an
// unknown or malformed value falls back to a safe member instead of failing the file it sits in, so
// one hand-edited recording entry does not take the whole metadata file down.
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

        // Structured tokens are consumed whole, or the serializer fails the read it was meant to
        // survive.
        reader.Skip();
        return ContentType.Recording;
    }

    public override void Write(Utf8JsonWriter writer, ContentType value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
