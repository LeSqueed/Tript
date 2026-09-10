// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Detection;
using Xunit;

namespace Tript.Detection.Tests;

public class NearBlackTests
{
    private const int W = 1920;
    private const int H = 1080;
    private const int Stride = 16;

    private static byte[] Frame() => new byte[W * H * 4];

    private static void Light(byte[] bgra, int x0, int y0, int w, int h, byte value = 200)
    {
        for (int y = y0; y < y0 + h; y++)
        {
            var row = y * W * 4;
            for (int x = x0; x < x0 + w; x++)
            {
                var i = row + x * 4;
                bgra[i] = value;
                bgra[i + 1] = value;
                bgra[i + 2] = value;
                bgra[i + 3] = 255;
            }
        }
    }

    [Fact]
    public void IsNearBlack_AllZeroFrame_IsNearBlack()
    {
        Assert.True(DetectionFramePreprocessor.IsNearBlack(Frame(), W, H));
    }

    [Fact]
    public void IsNearBlack_NoiseFrame_IsNotNearBlack()
    {
        var bgra = ReferenceImplementations.SyntheticBgra(W, H, 5);

        Assert.False(DetectionFramePreprocessor.IsNearBlack(bgra, W, H));
    }

    [Fact]
    public void IsNearBlack_TopLeftQuarterLit_IsNotNearBlack()
    {
        var bgra = Frame();
        Light(bgra, 0, 0, W / 2, H / 2);

        Assert.False(DetectionFramePreprocessor.IsNearBlack(bgra, W, H));
    }

    [Fact]
    public void IsNearBlack_SinglePixelOnSamplePoint_IsNotNearBlack()
    {
        var bgra = Frame();
        Light(bgra, Stride, Stride, 1, 1);

        Assert.False(DetectionFramePreprocessor.IsNearBlack(bgra, W, H));
    }

    [Fact]
    public void IsNearBlack_SinglePixelBetweenSamplePoints_IsMissedByDesign()
    {
        var bgra = Frame();
        const int y = H / 2;
        Assert.NotEqual(0, y % Stride);
        Light(bgra, W / 2, y, 1, 1);

        Assert.True(DetectionFramePreprocessor.IsNearBlack(bgra, W, H));
    }

    [Fact]
    public void IsNearBlack_BlobSmallerThanStride_IsMissedByDesign()
    {
        var bgra = Frame();
        Light(bgra, 1, 1, Stride - 1, Stride - 1);

        Assert.True(DetectionFramePreprocessor.IsNearBlack(bgra, W, H));
    }

    [Fact]
    public void IsNearBlack_StripCoveringTenSamplePoints_IsNotNearBlack()
    {
        var bgra = Frame();
        Light(bgra, 0, Stride, Stride * 10, Stride);

        Assert.False(DetectionFramePreprocessor.IsNearBlack(bgra, W, H));
    }
}
