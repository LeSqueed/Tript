// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Detection;
using Xunit;

namespace Tript.Detection.Tests;

public class PaddleOcrRecognizerTests
{
    [Fact]
    public void DecodeCtcCollapsesRepeatsAndBlanks()
    {
        var output = new float[]
        {
            0.01f, 0.90f, 0.09f,
            0.01f, 0.91f, 0.08f,
            0.95f, 0.03f, 0.02f,
            0.01f, 0.04f, 0.95f,
        };

        var result = PaddleOcrRecognizer.DecodeCtc(output, 4, 3, ["A", "B"]);

        Assert.Equal("AB", result.Text);
        Assert.Equal(0.925f, result.Confidence, 3);
    }

    [Fact]
    public void PrepareInputUsesBgrPlanesAndGrayPadding()
    {
        var bgra = new byte[]
        {
            0, 64, 255, 255,
            255, 128, 0, 255,
        };

        var result = PaddleOcrRecognizer.PrepareInput(bgra, 2, 1, 0, 0, 2, 1, 6, 2);

        Assert.Equal(36, result.Length);
        Assert.Equal(-1f, result[0]);
        Assert.InRange(result[12], -0.51f, -0.49f);
        Assert.Equal(1f, result[24]);
        Assert.Equal(0f, result[5]);
    }

    [Fact]
    public void DetectorResizeKeepsDimensionsWithinLimitAndAligned()
    {
        var result = PaddleOcrTextDetector.ResizeDimensions(1071, 268);

        Assert.Equal(960, result.Width);
        Assert.Equal(256, result.Height);
    }

    [Fact]
    public void DetectorFindsSeparateHorizontalRegions()
    {
        var probabilities = new float[12 * 8];
        Fill(probabilities, 12, 2, 0, 7, 3, 0.9f);
        Fill(probabilities, 12, 3, 5, 5, 3, 0.8f);

        var regions = PaddleOcrTextDetector.FindRegions(probabilities, 12, 8, 120, 80);

        Assert.Equal(2, regions.Count);
        Assert.True(regions[0].Y < regions[1].Y);
        Assert.All(regions, region => Assert.True(region.Confidence >= 0.8f));
    }

    [Fact]
    public void RegionPlannerSharesExactSegmentsAcrossTranslatedEvents()
    {
        var first = OcrDefinition(1, "en");
        var second = OcrDefinition(2, "de");

        var plans = OcrRegionPlanner.Build([first, second]);

        var plan = Assert.Single(plans);
        Assert.Equal(2, plan.Bindings.Count);
    }

    [Fact]
    public void RegionPlannerDoesNotMergeOverlappingSegments()
    {
        var first = OcrDefinition(1, "en");
        var second = OcrDefinition(2, "de");
        second.ScreenRegionX = 0.11f;

        Assert.Equal(2, OcrRegionPlanner.Build([first, second]).Count);
    }

    [Fact]
    public void RegionPlannerUsesRegionGroupOverEventOwnRegion()
    {
        var definition = OcrDefinition(1, "en");
        definition.RegionGroupId = 7;
        var group = new RegionGroupDefinition
        {
            Id = 7,
            Name = "Event Feed",
            ScreenRegionX = 0.5f,
            ScreenRegionY = 0.6f,
            ScreenRegionW = 0.3f,
            ScreenRegionH = 0.2f,
        };

        var plan = Assert.Single(OcrRegionPlanner.Build([definition], [group]));

        Assert.Equal(0.5f, plan.Region.X);
        Assert.Equal(0.6f, plan.Region.Y);
        Assert.Equal(0.3f, plan.Region.W);
        Assert.Equal(0.2f, plan.Region.H);
    }

    [Fact]
    public void RegionPlannerFallsBackToEventRegionWhenGroupMissing()
    {
        var definition = OcrDefinition(1, "en");
        definition.RegionGroupId = 7;
        var otherGroup = new RegionGroupDefinition { Id = 8, Name = "Other" };

        var plan = Assert.Single(OcrRegionPlanner.Build([definition], [otherGroup]));

        Assert.Equal(0.1f, plan.Region.X);
        Assert.Equal(0.2f, plan.Region.Y);
        Assert.Equal(0.4f, plan.Region.W);
        Assert.Equal(0.1f, plan.Region.H);
    }

    private static EventDefinition OcrDefinition(int id, string language) => new()
    {
        Id = id,
        Name = "ocr" + id,
        DetectionKind = DetectionKind.Ocr,
        ScreenRegionX = 0.1f,
        ScreenRegionY = 0.2f,
        ScreenRegionW = 0.4f,
        ScreenRegionH = 0.1f,
        Ocr = new OcrEventDefinition
        {
            Patterns = [new OcrPatternDefinition { LanguageTag = language, Template = "KILL {player}" }],
        },
    };

    private static void Fill(float[] values, int stride, int x, int y, int width, int height, float value)
    {
        for (var row = y; row < y + height; row++)
        for (var column = x; column < x + width; column++)
            values[row * stride + column] = value;
    }
}
