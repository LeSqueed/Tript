// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tript.Core;

// Reads and writes the member name, and falls back to Manual for anything it cannot make sense of
// — an unknown name, a value removed in an earlier version, a hand-edited file, a half-written
// one. The alternative is that one bad token fails the deserialization of the file it sits in,
// taking every other bookmark and the recording's metadata with it.
public sealed class BookmarkTypeConverter : JsonConverter<BookmarkType>
{
    // Null reaches Read rather than the serializer's own null handling, which would throw for a
    // non-nullable enum.
    public override bool HandleNull => true;

    public override BookmarkType Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && Enum.TryParse<BookmarkType>(reader.GetString(), ignoreCase: true, out var parsed)
            // TryParse also accepts the ordinal spelled as a string, which can name a member that
            // does not exist.
            && Enum.IsDefined(parsed))
        {
            return parsed;
        }

        // An object or an array has to be consumed whole. Leaving its inner tokens on the reader
        // makes the serializer fail the read it was meant to survive, complaining that the
        // converter read too little.
        reader.Skip();
        return BookmarkType.Manual;
    }

    public override void Write(Utf8JsonWriter writer, BookmarkType value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
