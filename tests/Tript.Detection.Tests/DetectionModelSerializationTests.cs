// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.Detection;
using Xunit;

namespace Tript.Detection.Tests;

public class DetectionModelSerializationTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void LegacyEventDefaultsToObjectDetection()
    {
        var definition = JsonSerializer.Deserialize<EventDefinition>(
            """{"id":1,"name":"Kill","type":"Trigger","classId":0}""", Options);

        Assert.NotNull(definition);
        Assert.Equal(DetectionKind.Object, definition.DetectionKind);
    }

    [Fact]
    public void OcrEventRoundTripsLanguagePatterns()
    {
        var definition = new EventDefinition
        {
            Id = 7,
            Name = "Turret elimination",
            Type = EventType.Subtractor,
            DetectionKind = DetectionKind.Ocr,
            Ocr = new OcrEventDefinition
            {
                Patterns =
                [
                    new OcrPatternDefinition { LanguageTag = "en", Template = "ELIMINATED {owner} TURRET" },
                    new OcrPatternDefinition { LanguageTag = "de", Template = "ELIMINIERT {owner} GESCHUETZ" },
                ],
            },
        };

        var json = JsonSerializer.Serialize(definition, Options);
        var roundTrip = JsonSerializer.Deserialize<EventDefinition>(json, Options);

        Assert.Contains("\"detectionKind\":\"Ocr\"", json);
        Assert.NotNull(roundTrip?.Ocr);
        Assert.Equal(["en", "de"], roundTrip.Ocr.Patterns.Select(pattern => pattern.LanguageTag));
    }
}
