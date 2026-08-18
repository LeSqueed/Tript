// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

// Reads timestamps out of the settings and metadata files without letting one bad timestamp fail
// the file it sits in — the same graceful-degradation contract as ContentTypeConverter and
// Tript.Core's BookmarkTypeConverter. What was measured first, because it decides what this
// converter must and must not do.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tript.Settings;

public sealed class TolerantDateTimeConverter : JsonConverter<DateTime>
{
    // A null timestamp is a missing timestamp, not a broken file.
    public override bool HandleNull => true;

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                // TryGetDateTime is the same ISO 8601-1:2019 reader the default converter uses, so
                // every form the app itself writes — offset, "Z", or neither — still round-trips
                // exactly as before.
                if (reader.TryGetDateTime(out var iso))
                    return iso;

                // A hand-edited or foreign format, tried once as a general parse before giving up.
                var text = reader.GetString();
                if (!string.IsNullOrWhiteSpace(text)
                    && DateTime.TryParse(text, CultureInfo.InvariantCulture,
                        DateTimeStyles.AllowWhiteSpaces, out var parsed))
                {
                    return parsed;
                }

                return default;

            case JsonTokenType.Number:
                // Epoch seconds, as a build that stored the instant numerically would have written
                // it. Read as local time so it lands in the same shape as DateTime.Now, which is
                // what the app writes.
                if (reader.TryGetInt64(out var epochSeconds))
                {
                    try
                    {
                        return DateTimeOffset.FromUnixTimeSeconds(epochSeconds).LocalDateTime;
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        // Out of the representable range: no timestamp, not a failed file.
                        return default;
                    }
                }

                return default;

            default:
                // Structured tokens are consumed whole, or the serializer fails the read it was
                // meant to survive.
                reader.Skip();
                return default;
        }
    }

    // The write side is unchanged from the default converter: the same ISO 8601 round-trip form the
    // existing files are written in, so nothing about the on-disk shape moves.
    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}
