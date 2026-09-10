// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System;
using Tript.Detection;
using Xunit;

namespace Tript.Detection.Tests;

public class VisualEventDetectorTests
{
    [Fact]
    public void BgraToGray_WeightsChannelsInBgraOrder()
    {
        const int w = 2;
        const int h = 2;

        byte[] bgra =
        [
            10, 20, 200, 255,
            200, 20, 10, 255,
            0, 255, 0, 255,
            255, 0, 0, 255,
        ];

        byte[] expected = [72, 37, 149, 29];

        var gray = DetectionFramePreprocessor.BgraToGray(bgra, w, h);

        Assert.True(gray.Length >= w * h);
        Assert.Equal(expected, gray.AsSpan(0, w * h).ToArray());
    }
}
