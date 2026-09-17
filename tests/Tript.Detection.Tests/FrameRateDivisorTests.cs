// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System;
using Tript.Detection;
using Xunit;

namespace Tript.Detection.Tests;

public class FrameRateDivisorTests
{
    [Theory]
    [InlineData(30, 10)]
    [InlineData(60, 20)]
    [InlineData(120, 40)]
    [InlineData(144, 48)]
    [InlineData(240, 80)]
    public void DerivesDivisorFromOutputFps(int outputFps, int expected)
    {
        Assert.Equal(expected, DetectionCaptureSettings.ComputeFrameRateDivisor(outputFps));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(240)]
    public void ResultingCaptureRateStaysAboveConsumptionRate(int outputFps)
    {
        var divisor = DetectionCaptureSettings.ComputeFrameRateDivisor(outputFps);
        var captureFps = (double)outputFps / divisor;

        Assert.True(captureFps >= 2.0, $"{outputFps}fps/{divisor} = {captureFps:F2} Hz");

        Assert.True(captureFps <= 4.0, $"{outputFps}fps/{divisor} = {captureFps:F2} Hz");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void FallsBackToConstantWhenFpsUnavailable(int outputFps)
    {
        Assert.Equal(30, DetectionCaptureSettings.ComputeFrameRateDivisor(outputFps));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NeverReturnsZeroForLowFps(int outputFps)
    {
        Assert.True(DetectionCaptureSettings.ComputeFrameRateDivisor(outputFps) >= 1);
    }

    [Fact]
    public void FractionalRateRoundsToNearestWholeFps()
    {
        var fps = (int)Math.Round(60000.0 / 1001.0);
        Assert.Equal(60, fps);
        Assert.Equal(20, DetectionCaptureSettings.ComputeFrameRateDivisor(fps));
    }

    [Fact]
    public void BareNumeratorWouldProduceAWildlyWrongDivisor()
    {
        Assert.NotEqual(
            DetectionCaptureSettings.ComputeFrameRateDivisor(60),
            DetectionCaptureSettings.ComputeFrameRateDivisor(60000));
    }
}
