// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Buffers;

namespace Tript.Detection;

internal static class BgraBilinearSampler
{
    internal static void Validate(byte[] bgra, int frameWidth, int frameHeight,
        int cropX, int cropY, int cropWidth, int cropHeight)
    {
        if (frameWidth <= 0 || frameHeight <= 0 || cropWidth <= 0 || cropHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(cropWidth));
        if (cropX < 0 || cropY < 0 || cropX + cropWidth > frameWidth || cropY + cropHeight > frameHeight)
            throw new ArgumentOutOfRangeException(nameof(cropX));
        if (bgra.Length < checked(frameWidth * frameHeight * 4))
            throw new ArgumentException("BGRA frame buffer is too short.", nameof(bgra));
    }

    internal static void SamplePlanes(byte[] bgra, int frameWidth, int cropX, int cropY,
        int cropWidth, int cropHeight, int sampleWidth, int sampleHeight, int planeWidth, Span<float> output)
    {
        var planeSize = planeWidth * sampleHeight;
        var left = ArrayPool<int>.Shared.Rent(sampleWidth);
        var right = ArrayPool<int>.Shared.Rent(sampleWidth);
        var xWeights = ArrayPool<double>.Shared.Rent(sampleWidth);
        try
        {
            for (var targetX = 0; targetX < sampleWidth; targetX++)
            {
                var x = (targetX + 0.5) * cropWidth / sampleWidth - 0.5;
                Axis(x, cropWidth, out var x0, out var x1, out xWeights[targetX]);
                left[targetX] = (cropX + x0) * 4;
                right[targetX] = (cropX + x1) * 4;
            }

            for (var targetY = 0; targetY < sampleHeight; targetY++)
            {
                var y = (targetY + 0.5) * cropHeight / sampleHeight - 0.5;
                Axis(y, cropHeight, out var y0, out var y1, out var yWeight);
                var top = (cropY + y0) * frameWidth * 4;
                var bottom = (cropY + y1) * frameWidth * 4;
                var rowStart = targetY * planeWidth;

                for (var targetX = 0; targetX < sampleWidth; targetX++)
                {
                    var target = rowStart + targetX;
                    var xWeight = xWeights[targetX];
                    var topLeft = top + left[targetX];
                    var topRight = top + right[targetX];
                    var bottomLeft = bottom + left[targetX];
                    var bottomRight = bottom + right[targetX];
                    for (var channel = 0; channel < 3; channel++)
                    {
                        output[planeSize * channel + target] = Blend(
                            bgra[topLeft + channel], bgra[topRight + channel],
                            bgra[bottomLeft + channel], bgra[bottomRight + channel], xWeight, yWeight);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(left);
            ArrayPool<int>.Shared.Return(right);
            ArrayPool<double>.Shared.Return(xWeights);
        }
    }

    private static void Axis(double position, int length, out int first, out int second, out double weight)
    {
        position = Math.Clamp(position, 0, length - 1);
        first = Math.Clamp((int)Math.Floor(position), 0, length - 1);
        second = Math.Min(first + 1, length - 1);
        weight = Math.Clamp(position - Math.Floor(position), 0, 1);
    }

    private static float Blend(byte topLeft, byte topRight, byte bottomLeft, byte bottomRight,
        double xWeight, double yWeight)
    {
        var top = topLeft + (topRight - topLeft) * xWeight;
        var bottom = bottomLeft + (bottomRight - bottomLeft) * xWeight;
        return (float)(top + (bottom - top) * yWeight);
    }
}
