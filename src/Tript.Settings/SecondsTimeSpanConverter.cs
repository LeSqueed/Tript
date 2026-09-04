// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

// The seam between the wire's numbers and the recorder's TimeSpans, for the settings fields whose
// contract is "seconds". The settings UI sends whole seconds as JSON numbers (the frontend settings
// model is numeric end to end), and a number is what the frontend reads back off a settings push.
// Before the seam existed the settings file stored the value as a TimeSpan string ("00:00:10"), so
// the read side accepts that form too; the write side always uses the number, which is what keeps
// a settings patch from failing to deserialize.
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
                // The form the settings UI sends. Non-finite and out-of-range values are treated
                // like a missing value rather than a failed file: the consumer already maps a
                // non-positive timeout to its default (CapturePolicy.From), so the degradation is
                // the same one a zero would get.
                if (reader.TryGetDouble(out var seconds)
                    && double.IsFinite(seconds)
                    && seconds >= 0
                    && seconds < TimeSpan.MaxValue.TotalSeconds)
                {
                    return TimeSpan.FromSeconds(seconds);
                }

                return default;

            case JsonTokenType.String:
                // The form the settings file carried before the seam: an ISO 8601 duration.
                var text = reader.GetString();
                return text is not null
                    && TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : default;

            default:
                // Structured tokens are consumed whole, or the serializer fails the read it was
                // meant to survive.
                reader.Skip();
                return default;
        }
    }

    // Whole seconds as a number: the exact shape the settings UI reads and sends back.
    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value.TotalSeconds);
}
