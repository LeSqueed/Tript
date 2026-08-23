// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Detection;
using Xunit;

namespace Tript.Detection.Tests;

public sealed class DetectionBatchCounterTests
{
    private static DetectionResult Box(float x, float y = 0.3f, float w = 0.12f, float h = 0.06f) => new()
    {
        X = x,
        Y = y,
        Width = w,
        Height = h,
    };

    [Fact]
    public void DistinctDetections_CollapsesOverlappingBoxes()
    {
        var detections = DetectionBatchCounter.DistinctDetections([
            Box(0.4f),
            Box(0.404f, y: 0.297f, w: 0.117f, h: 0.062f),
            Box(0.396f, y: 0.302f, w: 0.123f, h: 0.059f),
        ]);

        Assert.Single(detections);
    }

    [Fact]
    public void DistinctDetections_LeavesDisjointInstancesSeparate()
    {
        var detections = DetectionBatchCounter.DistinctDetections([
            Box(0.1f),
            Box(0.7f, y: 0.6f),
        ]);

        Assert.Equal(2, detections.Count);
    }

    [Fact]
    public void DistinctDetections_IsIndependentOfInputOrder()
    {
        var first = DetectionBatchCounter.DistinctDetections([
            Box(0.1f, w: 0.2f),
            Box(0.2f, w: 0.2f),
            Box(0.3f, w: 0.2f),
        ]);
        var second = DetectionBatchCounter.DistinctDetections([
            Box(0.3f, w: 0.2f),
            Box(0.1f, w: 0.2f),
            Box(0.2f, w: 0.2f),
        ]);

        Assert.Equal(first.Count, second.Count);
    }

    [Fact]
    public void DistinctDetections_TreatsZeroAreaBoxesAsOneInstance()
    {
        var detections = DetectionBatchCounter.DistinctDetections([
            Box(0.4f, w: 0),
            Box(0.8f, w: 0),
        ]);

        Assert.Single(detections);
    }

}
