// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tript.Settings;

public sealed class SecondsTimeSpanConverter : JsonConverter<TimeSpan>
{
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                if (reader.TryGetDouble(out var seconds)
                    && double.IsFinite(seconds)
                    && seconds >= 0
                    && seconds < TimeSpan.MaxValue.TotalSeconds)
                {
                    return TimeSpan.FromSeconds(seconds);
                }

                return default;

            case JsonTokenType.String:
                var text = reader.GetString();
                return text is not null
                    && TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : default;

            default:
                reader.Skip();
                return default;
        }
    }

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value.TotalSeconds);
}
