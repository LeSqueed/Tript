// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System;
using System.Collections.Generic;
using System.Linq;
using Tript.Detection;
using Xunit;

namespace Tript.Detection.Tests;

public class ParseYoloOutputDecodeTests
{
    private static List<DetectionResult> Parse(ReadOnlySpan<float> output, int inputSize, int numClasses)
        => DetectionFramePreprocessor.ParseYoloOutputForInput(output, inputSize, inputSize, numClasses);

    private sealed record Anchor(float Cx, float Cy, float W, float H, float[] Confidences);

    private static float[] Tensor(params Anchor[] anchors)
    {
        var rows = new List<IEnumerable<float>>
        {
            anchors.Select(a => a.Cx),
            anchors.Select(a => a.Cy),
            anchors.Select(a => a.W),
            anchors.Select(a => a.H),
        };

        var numClasses = anchors[0].Confidences.Length;
        for (int c = 0; c < numClasses; c++)
        {
            var classIndex = c;
            rows.Add(anchors.Select(a => a.Confidences[classIndex]));
        }

        return rows.SelectMany(row => row).ToArray();
    }

    [Fact]
    public void DecodesAnchorsAtTheChannelMajorRowOffsets()
    {
        var output = Tensor(
            new Anchor(50f, 40f, 20f, 10f, [0.10f, 0.90f, 0.20f]),
            new Anchor(10f, 90f, 4f, 6f, [0.69f, 0.10f, 0.20f]),
            new Anchor(30f, 70f, 8f, 12f, [0.10f, 0.20f, 0.71f]),
            new Anchor(80f, 20f, 40f, 4f, [0.72f, 0.30f, 0.71f]),
            new Anchor(0f, 0f, 0f, 0f, [0f, 0f, 0f]));

        var results = Parse(output, 100, 3);

        Assert.Equal(3, results.Count);

        Assert.Equal(1, results[0].ClassId);
        Assert.Equal(0.9, results[0].Confidence, 4);
        Assert.Equal(0.4, results[0].X, 4);
        Assert.Equal(0.35, results[0].Y, 4);
        Assert.Equal(0.2, results[0].Width, 4);
        Assert.Equal(0.1, results[0].Height, 4);

        Assert.Equal(2, results[1].ClassId);
        Assert.Equal(0.71, results[1].Confidence, 4);
        Assert.Equal(0.26, results[1].X, 4);
        Assert.Equal(0.64, results[1].Y, 4);
        Assert.Equal(0.08, results[1].Width, 4);
        Assert.Equal(0.12, results[1].Height, 4);

        Assert.Equal(0, results[2].ClassId);
        Assert.Equal(0.72, results[2].Confidence, 4);
        Assert.Equal(0.6, results[2].X, 4);
        Assert.Equal(0.18, results[2].Y, 4);
        Assert.Equal(0.4, results[2].Width, 4);
        Assert.Equal(0.04, results[2].Height, 4);
    }

    [Fact]
    public void ReadsConfidencesFromRowFour_NotFromTheHeightRow()
    {
        float[] output =
        [
            100f, 200f,
            300f, 400f,
            500f, 600f,
            800f, 900f,
            0.50f, 0.95f,
        ];

        var results = Parse(output, 1000, 1);

        var only = Assert.Single(results);
        Assert.Equal(0, only.ClassId);
        Assert.Equal(0.95, only.Confidence, 4);

        Assert.Equal(0.2 - 0.3, only.X, 4);
        Assert.Equal(0.4 - 0.45, only.Y, 4);
        Assert.Equal(0.6, only.Width, 4);
        Assert.Equal(0.9, only.Height, 4);
    }

    [Fact]
    public void DropsAnchorsBelowThePointSevenCutoff_AndKeepsThoseAtOrAbove()
    {
        Assert.Empty(Parse(Tensor(new Anchor(50f, 50f, 10f, 10f, [0.69f])), 100, 1));
        Assert.Single(Parse(Tensor(new Anchor(50f, 50f, 10f, 10f, [0.70f])), 100, 1));
        Assert.Single(Parse(Tensor(new Anchor(50f, 50f, 10f, 10f, [0.71f])), 100, 1));
    }

    [Fact]
    public void SelectsTheHighestScoringClassAcrossAllClassRows()
    {
        var output = Tensor(
            new Anchor(10f, 10f, 2f, 2f, [0.95f, 0.80f, 0.75f, 0.71f]),
            new Anchor(20f, 20f, 2f, 2f, [0.75f, 0.71f, 0.96f, 0.80f]),
            new Anchor(30f, 30f, 2f, 2f, [0.71f, 0.75f, 0.80f, 0.97f]));

        var results = Parse(output, 100, 4);

        Assert.Equal(new[] { 0, 2, 3 }, results.Select(r => r.ClassId).ToArray());
        Assert.Equal(0.95, results[0].Confidence, 4);
        Assert.Equal(0.96, results[1].Confidence, 4);
        Assert.Equal(0.97, results[2].Confidence, 4);
    }

    [Fact]
    public void OnATie_KeepsTheLowestClassId()
    {
        var results = Parse(Tensor(new Anchor(50f, 50f, 10f, 10f, [0.4f, 0.88f, 0.88f])), 100, 3);

        Assert.Equal(1, Assert.Single(results).ClassId);
    }

    [Fact]
    public void ConvertsCentreBoxesToNormalizedTopLeftCorners()
    {
        var results = Parse(Tensor(
            new Anchor(320f, 160f, 64f, 32f, [0.9f]),
            new Anchor(0f, 0f, 100f, 200f, [0.9f])), 640, 1);

        Assert.Equal(0.45, results[0].X, 4);
        Assert.Equal(0.225, results[0].Y, 4);
        Assert.Equal(0.1, results[0].Width, 4);
        Assert.Equal(0.05, results[0].Height, 4);

        Assert.Equal(-0.078125, results[1].X, 6);
        Assert.Equal(-0.15625, results[1].Y, 6);
    }

    [Fact]
    public void AWrongClassCount_SilentlyDecodesTheSameBufferToRelocatedBoxes()
    {
        var output = Tensor(
            new Anchor(10f, 20f, 2f, 2f, [0.9f, 0f, 0f]),
            new Anchor(11f, 21f, 2f, 2f, [0.9f, 0f, 0f]),
            new Anchor(12f, 22f, 2f, 2f, [0.9f, 0f, 0f]),
            new Anchor(13f, 23f, 2f, 2f, [0.9f, 0f, 0f]),
            new Anchor(14f, 24f, 2f, 2f, [0.9f, 0f, 0f]),
            new Anchor(15f, 25f, 2f, 2f, [0.9f, 0f, 0f]));

        Assert.Equal(42, output.Length);

        var correct = Parse(output, 100, 3);
        var wrong = Parse(output, 100, 2);

        Assert.Equal(6, correct.Count);
        Assert.Equal(0.09, correct[0].X, 4);
        Assert.Equal(0.19, correct[0].Y, 4);

        Assert.Equal(2, wrong.Count);
        Assert.Equal(0.09, wrong[0].X, 4);
        Assert.Equal(0.20, wrong[0].Y, 4);
        Assert.Equal(0.10, wrong[1].X, 4);
        Assert.Equal(0.21, wrong[1].Y, 4);
    }
}
