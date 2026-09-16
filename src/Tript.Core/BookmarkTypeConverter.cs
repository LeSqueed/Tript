// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tript.Core;

// Falls back to Manual so one bad token cannot fail the whole metadata file.
public sealed class BookmarkTypeConverter : JsonConverter<BookmarkType>
{
    // A null token must reach Read: the serializer's own null handling throws for this enum.
    public override bool HandleNull => true;

    public override BookmarkType Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && Enum.TryParse<BookmarkType>(reader.GetString(), ignoreCase: true, out var parsed)
            // TryParse also accepts ordinals, which can name a member that does not exist.
            && Enum.IsDefined(parsed))
        {
            return parsed;
        }

        // Consume the whole value, or the serializer fails the read this converter exists to survive.
        reader.Skip();
        return BookmarkType.Manual;
    }

    public override void Write(Utf8JsonWriter writer, BookmarkType value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
