// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Detection;
using Xunit;

namespace Tript.Detection.Tests;

public class CopyPlaneTests
{
    private const int Width = 64;
    private const int Height = 32;
    private const int RowBytes = Width * 4;

    private static byte[] Source(int stride)
    {
        var src = new byte[stride * Height];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < RowBytes; x++)
            {
                src[y * stride + x] = (byte)((y * 31 + x * 7) & 0x7F);
            }

            for (int p = RowBytes; p < stride; p++)
            {
                src[y * stride + p] = 0xEE;
            }
        }
        return src;
    }

    private static byte[] Expected()
    {
        var dst = new byte[RowBytes * Height];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < RowBytes; x++)
            {
                dst[y * RowBytes + x] = (byte)((y * 31 + x * 7) & 0x7F);
            }
        }
        return dst;
    }

    [Fact]
    public void TightStrideCopiesWholePlane()
    {
        var src = Source(RowBytes);
        var dst = new byte[RowBytes * Height];

        DetectionFramePreprocessor.CopyPlane(src, RowBytes, dst, RowBytes, Height);

        Assert.Equal(Expected(), dst);
    }

    [Theory]
    [InlineData(RowBytes + 16)]
    [InlineData(RowBytes + 64)]
    [InlineData(RowBytes + 256)]
    public void PaddedStrideSkipsPadding(int stride)
    {
        var src = Source(stride);
        var dst = new byte[RowBytes * Height];

        DetectionFramePreprocessor.CopyPlane(src, stride, dst, RowBytes, Height);

        Assert.Equal(Expected(), dst);
        Assert.DoesNotContain((byte)0xEE, dst);
    }

    [Fact]
    public void BothBranchesAgree()
    {
        const int Padded = RowBytes + 128;

        var tightDst = new byte[RowBytes * Height];
        DetectionFramePreprocessor.CopyPlane(Source(RowBytes), RowBytes, tightDst, RowBytes, Height);

        var paddedDst = new byte[RowBytes * Height];
        DetectionFramePreprocessor.CopyPlane(Source(Padded), Padded, paddedDst, RowBytes, Height);

        Assert.Equal(tightDst, paddedDst);
    }

    [Fact]
    public void ToleratesOversizedDestination()
    {
        var src = Source(RowBytes);
        var dst = new byte[RowBytes * Height + 4096];

        DetectionFramePreprocessor.CopyPlane(src, RowBytes, dst, RowBytes, Height);

        Assert.Equal(Expected(), dst[..(RowBytes * Height)]);
    }
}
