// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System;
using Tript.Detection;
using Xunit;

namespace Tript.Detection.Tests;

public class TableResamplingGoldenTests
{
    public static TheoryData<int, int, int, int, int, int> Crops => new()
    {
        { 1920, 1080, 0, 0, 1920, 1080 },
        { 1920, 1080, 497, 551, 1032, 341 },
        { 1920, 1080, 18, 12, 416, 74 },
        { 1920, 1080, 1919, 0, 1, 1080 },
        { 640, 360, 10, 20, 1, 1 },
        { 640, 360, 5, 7, 37, 3 },
        { 300, 200, 0, 0, 300, 200 },
    };

    [Theory]
    [MemberData(nameof(Crops))]
    public void CropAndResizeGray_MatchesThePerPixelVersion(int frameW, int frameH, int x, int y, int w, int h)
    {
        var gray = Gray(frameW, frameH, seed: x + y);

        var expected = PerPixel.CropAndResizeGray(gray, frameW, x, y, w, h, 640, 640);
        var actual = DetectionFramePreprocessor.CropAndResizeGray(gray, frameW, frameH, x, y, w, h, 640, 640);

        Assert.Equal(expected, actual.AsSpan(0, expected.Length).ToArray());
    }

    [Theory]
    [MemberData(nameof(Crops))]
    public void RecognizerInput_MatchesThePerPixelVersion(int frameW, int frameH, int x, int y, int w, int h)
    {
        var bgra = ReferenceImplementations.SyntheticBgra(frameW, frameH, x * 7 + y);

        var expected = PerPixel.RecognizerInput(bgra, frameW, frameH, x, y, w, h, 320, 48);
        var actual = PaddleOcrRecognizer.PrepareInput(bgra, frameW, frameH, x, y, w, h, 320, 48);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RecognizerInput_ClearsPaddingLeftFromAWiderCrop()
    {
        var bgra = ReferenceImplementations.SyntheticBgra(640, 360, 3);
        var buffer = new float[320 * 48 * 3];

        PaddleOcrRecognizer.PrepareInput(bgra, 640, 360, 0, 0, 600, 40, 320, 48, buffer);
        PaddleOcrRecognizer.PrepareInput(bgra, 640, 360, 0, 0, 30, 40, 320, 48, buffer);

        Assert.Equal(PerPixel.RecognizerInput(bgra, 640, 360, 0, 0, 30, 40, 320, 48), buffer);
    }

    [Theory]
    [MemberData(nameof(Crops))]
    public void DetectorInput_MatchesThePerPixelVersion(int frameW, int frameH, int x, int y, int w, int h)
    {
        var bgra = ReferenceImplementations.SyntheticBgra(frameW, frameH, x + y * 3);
        var (inputW, inputH) = PaddleOcrTextDetector.ResizeDimensions(w, h);

        var expected = PerPixel.DetectorInput(bgra, frameW, frameH, x, y, w, h, inputW, inputH);
        var actual = PaddleOcrTextDetector.PrepareInput(bgra, frameW, frameH, x, y, w, h, inputW, inputH);

        Assert.Equal(expected, actual);
    }

    private static byte[] Gray(int w, int h, int seed)
    {
        var gray = new byte[w * h];
        new Random(seed).NextBytes(gray);
        return gray;
    }

    private static class PerPixel
    {
        internal static byte[] CropAndResizeGray(byte[] srcGray, int srcW,
            int cropX, int cropY, int cropW, int cropH, int dstW, int dstH)
        {
            var crop = new byte[cropW * cropH];
            for (int y = 0; y < cropH; y++)
                Array.Copy(srcGray, (cropY + y) * srcW + cropX, crop, y * cropW, cropW);

            var maxX = cropW - 1;
            var maxY = cropH - 1;
            var dst = new byte[dstW * dstH];
            for (int dy = 0; dy < dstH; dy++)
            {
                float sy = (dy + 0.5f) * cropH / dstH - 0.5f;
                if (sy < 0) sy = 0;
                if (sy >= maxY) sy = Math.Max(cropH - 1.001f, 0f);
                int sy0 = (int)sy, sy1 = Math.Min(sy0 + 1, maxY);
                float fy = sy - sy0;

                for (int dx = 0; dx < dstW; dx++)
                {
                    float sx = (dx + 0.5f) * cropW / dstW - 0.5f;
                    if (sx < 0) sx = 0;
                    if (sx >= maxX) sx = Math.Max(cropW - 1.001f, 0f);
                    int sx0 = (int)sx, sx1 = Math.Min(sx0 + 1, maxX);
                    float fx = sx - sx0;

                    var v = (1 - fx) * (1 - fy) * crop[sy0 * cropW + sx0]
                          + fx * (1 - fy) * crop[sy0 * cropW + sx1]
                          + (1 - fx) * fy * crop[sy1 * cropW + sx0]
                          + fx * fy * crop[sy1 * cropW + sx1];
                    dst[dy * dstW + dx] = (byte)v;
                }
            }

            return dst;
        }

        internal static float[] RecognizerInput(byte[] bgra, int frameWidth, int frameHeight,
            int cropX, int cropY, int cropWidth, int cropHeight, int inputWidth, int inputHeight)
        {
            var resizedWidth = Math.Min(inputWidth,
                Math.Max(1, (int)Math.Round((double)cropWidth * inputHeight / cropHeight)));
            var planeSize = inputWidth * inputHeight;
            var output = new float[planeSize * 3];
            for (var targetY = 0; targetY < inputHeight; targetY++)
            {
                var sourceY = (targetY + 0.5) * cropHeight / inputHeight - 0.5;
                for (var targetX = 0; targetX < resizedWidth; targetX++)
                {
                    var sourceX = (targetX + 0.5) * cropWidth / resizedWidth - 0.5;
                    var target = targetY * inputWidth + targetX;
                    for (var channel = 0; channel < 3; channel++)
                    {
                        output[planeSize * channel + target] = SampleBilinear(bgra, frameWidth, cropX, cropY,
                            cropWidth, cropHeight, sourceX, sourceY, channel) / 127.5f - 1f;
                    }
                }
            }

            return output;
        }

        internal static float[] DetectorInput(byte[] bgra, int frameWidth, int frameHeight,
            int cropX, int cropY, int cropWidth, int cropHeight, int inputWidth, int inputHeight)
        {
            var planeSize = inputWidth * inputHeight;
            var output = new float[planeSize * 3];
            for (var targetY = 0; targetY < inputHeight; targetY++)
            {
                var sourceY = (targetY + 0.5) * cropHeight / inputHeight - 0.5;
                for (var targetX = 0; targetX < inputWidth; targetX++)
                {
                    var sourceX = (targetX + 0.5) * cropWidth / inputWidth - 0.5;
                    var target = targetY * inputWidth + targetX;
                    output[target] = (SampleBilinear(bgra, frameWidth, cropX, cropY,
                        cropWidth, cropHeight, sourceX, sourceY, 0) / 255f - 0.485f) / 0.229f;
                    output[planeSize + target] = (SampleBilinear(bgra, frameWidth,
                        cropX, cropY, cropWidth, cropHeight, sourceX, sourceY, 1) / 255f - 0.456f) / 0.224f;
                    output[planeSize * 2 + target] = (SampleBilinear(bgra, frameWidth,
                        cropX, cropY, cropWidth, cropHeight, sourceX, sourceY, 2) / 255f - 0.406f) / 0.225f;
                }
            }

            return output;
        }

        private static float SampleBilinear(byte[] bgra, int frameWidth, int cropX, int cropY,
            int cropWidth, int cropHeight, double x, double y, int channel)
        {
            x = Math.Clamp(x, 0, cropWidth - 1);
            y = Math.Clamp(y, 0, cropHeight - 1);
            var x0 = Math.Clamp((int)Math.Floor(x), 0, cropWidth - 1);
            var y0 = Math.Clamp((int)Math.Floor(y), 0, cropHeight - 1);
            var x1 = Math.Min(x0 + 1, cropWidth - 1);
            var y1 = Math.Min(y0 + 1, cropHeight - 1);
            var xWeight = Math.Clamp(x - Math.Floor(x), 0, 1);
            var yWeight = Math.Clamp(y - Math.Floor(y), 0, 1);
            var topLeft = bgra[((cropY + y0) * frameWidth + cropX + x0) * 4 + channel];
            var topRight = bgra[((cropY + y0) * frameWidth + cropX + x1) * 4 + channel];
            var bottomLeft = bgra[((cropY + y1) * frameWidth + cropX + x0) * 4 + channel];
            var bottomRight = bgra[((cropY + y1) * frameWidth + cropX + x1) * 4 + channel];
            var top = topLeft + (topRight - topLeft) * xWeight;
            var bottom = bottomLeft + (bottomRight - bottomLeft) * xWeight;
            return (float)(top + (bottom - top) * yWeight);
        }
    }
}
