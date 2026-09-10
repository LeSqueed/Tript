// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tript.Settings;

public sealed class TolerantDateTimeConverter : JsonConverter<DateTime>
{
    public override bool HandleNull => true;

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:

                if (reader.TryGetDateTime(out var iso))
                    return iso;

                var text = reader.GetString();
                if (!string.IsNullOrWhiteSpace(text)
                    && DateTime.TryParse(text, CultureInfo.InvariantCulture,
                        DateTimeStyles.AllowWhiteSpaces, out var parsed))
                {
                    return parsed;
                }

                return default;

            case JsonTokenType.Number:

                if (reader.TryGetInt64(out var epochSeconds))
                {
                    try
                    {
                        return DateTimeOffset.FromUnixTimeSeconds(epochSeconds).LocalDateTime;
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        return default;
                    }
                }

                return default;

            default:

                reader.Skip();
                return default;
        }
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}
