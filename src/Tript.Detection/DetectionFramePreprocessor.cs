// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Serilog;
using Serilog.Events;

namespace Tript.Detection;

internal enum GrayscaleStrategy
{
    PerGroupCrop,

    WholeFrameOnce
}

internal static class DetectionFramePreprocessor
{
    private const int BlackCheckStride = 16;
    private const int BlackCheckLumaThreshold = 15;
    private const int BlackCheckMinBrightSamples = 0;

    internal static void MapDetectionsToFullFrame(List<DetectionResult> detections,
        int cropX, int cropY, int cropW, int cropH, int frameW, int frameH)
    {
        foreach (var det in detections)
        {
            det.X = (det.X * cropW + cropX) / frameW;
            det.Y = (det.Y * cropH + cropY) / frameH;
            det.Width = det.Width * cropW / frameW;
            det.Height = det.Height * cropH / frameH;
        }
    }

    internal static void FilterDetectionsToEventRegions(List<DetectionResult> detections,
        IReadOnlyList<EventDefinition> definitions)
    {
        var definitionsByClass = definitions
            .Where(definition => definition.DetectionKind == DetectionKind.Object)
            .GroupBy(definition => definition.ClassId)
            .ToDictionary(group => group.Key, group => group.First());

        detections.RemoveAll(detection => !definitionsByClass.TryGetValue(detection.ClassId, out var definition)
            || !ContainsCenter(definition, detection));
    }

    private const float RegionContainmentToleranceFraction = 0.02f;

    private static bool ContainsCenter(EventDefinition definition, DetectionResult detection)
    {
        if (definition.ScreenRegionW is not > 0 || definition.ScreenRegionH is not > 0)
            return true;

        var centerX = detection.X + detection.Width / 2;
        var centerY = detection.Y + detection.Height / 2;
        var regionX = (definition.ScreenRegionX ?? 0) - RegionContainmentToleranceFraction;
        var regionY = (definition.ScreenRegionY ?? 0) - RegionContainmentToleranceFraction;
        var regionRight = (definition.ScreenRegionX ?? 0) + definition.ScreenRegionW.Value
            + RegionContainmentToleranceFraction;
        var regionBottom = (definition.ScreenRegionY ?? 0) + definition.ScreenRegionH.Value
            + RegionContainmentToleranceFraction;
        return centerX >= regionX && centerX <= regionRight
            && centerY >= regionY && centerY <= regionBottom;
    }

    internal static List<RegionGroup> BuildRegionGroups(List<EventDefinition> definitions)
    {
        var groups = new List<RegionGroup>();
        bool hasFullFrame = false;

        foreach (var def in definitions)
        {
            if (def.ScreenRegionW.HasValue && def.ScreenRegionW.Value > 0)
            {
                groups.Add(new RegionGroup
                {
                    X = def.ScreenRegionX ?? 0,
                    Y = def.ScreenRegionY ?? 0,
                    W = def.ScreenRegionW.Value,
                    H = def.ScreenRegionH ?? 0
                });
            }
            else
            {
                hasFullFrame = true;
            }
        }

        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < groups.Count && !changed; i++)
            {
                for (int j = i + 1; j < groups.Count; j++)
                {
                    if (!RegionsOverlap(groups[i], groups[j])) continue;
                    MergeRegions(groups[i], groups[j]);
                    groups.RemoveAt(j);
                    changed = true;
                    break;
                }
            }
        }

        if (hasFullFrame)
            groups.Add(new RegionGroup { X = 0, Y = 0, W = 1, H = 1 });

        if (groups.Count == 0)
            groups.Add(new RegionGroup { X = 0, Y = 0, W = 1, H = 1 });

        return groups;
    }

    internal static GrayscaleStrategy SelectGrayscaleStrategy(IReadOnlyList<RegionGroup> groups)
    {
        float coverage = 0f;
        foreach (var g in groups)
            coverage += g.W * g.H;

        return coverage > 1f ? GrayscaleStrategy.WholeFrameOnce : GrayscaleStrategy.PerGroupCrop;
    }

    internal static int CountGrayscalePixels(IReadOnlyList<RegionGroup> groups, int frameW, int frameH)
    {
        if (SelectGrayscaleStrategy(groups) == GrayscaleStrategy.WholeFrameOnce)
            return frameW * frameH;

        var total = 0;
        foreach (var g in groups)
        {
            if (TryGetCropRect(g, frameW, frameH, out _, out _, out var cropW, out var cropH))
                total += cropW * cropH;
        }

        return total;
    }

    internal static bool TryGetCropRect(RegionGroup group, int frameW, int frameH,
        out int cropX, out int cropY, out int cropW, out int cropH)
    {
        cropX = (int)(group.X * frameW);
        cropY = (int)(group.Y * frameH);
        cropW = (int)(group.W * frameW);
        cropH = (int)(group.H * frameH);

        if (cropW <= 0 || cropH <= 0) return false;
        if (cropX + cropW > frameW) cropW = frameW - cropX;
        if (cropY + cropH > frameH) cropH = frameH - cropY;
        return cropW > 0 && cropH > 0;
    }

    internal static bool RegionsOverlap(RegionGroup a, RegionGroup b)
    {
        if (a.X < b.X + b.W && a.X + a.W > b.X && a.Y < b.Y + b.H && a.Y + a.H > b.Y)
            return true;

        if (a.X >= b.X && a.Y >= b.Y && a.X + a.W <= b.X + b.W && a.Y + a.H <= b.Y + b.H)
            return true;

        if (b.X >= a.X && b.Y >= a.Y && b.X + b.W <= a.X + a.W && b.Y + b.H <= a.Y + a.H)
            return true;

        return false;
    }

    internal static void MergeRegions(RegionGroup a, RegionGroup b)
    {
        float x = Math.Min(a.X, b.X);
        float y = Math.Min(a.Y, b.Y);
        a.W = Math.Max(a.X + a.W, b.X + b.W) - x;
        a.H = Math.Max(a.Y + a.H, b.Y + b.H) - y;
        a.X = x;
        a.Y = y;
    }

    internal static void CopyPlane(ReadOnlySpan<byte> src, int srcStride, byte[] dst, int rowBytes, int height)
    {
        if (srcStride == rowBytes)
        {
            src.Slice(0, height * rowBytes).CopyTo(dst);
            return;
        }

        for (int y = 0; y < height; y++)
        {
            src.Slice(y * srcStride, rowBytes)
               .CopyTo(new Span<byte>(dst, y * rowBytes, rowBytes));
        }
    }

    internal static bool IsNearBlack(byte[] bgra, int w, int h)
    {
        var srcRowStride = w * 4;
        var bright = 0;

        for (int y = 0; y < h; y += BlackCheckStride)
        {
            var rowOffset = y * srcRowStride;
            for (int x = 0; x < w; x += BlackCheckStride)
            {
                var i = rowOffset + x * 4;
                var b = bgra[i];
                var g = bgra[i + 1];
                var r = bgra[i + 2];
                var luma = (byte)(0.299f * r + 0.587f * g + 0.114f * b);
                if (luma > BlackCheckLumaThreshold && ++bright > BlackCheckMinBrightSamples)
                    return false;
            }
        }

        return true;
    }

    internal static byte[] BgraToGray(byte[] bgra, int w, int h)
    {
        var pixels = w * h;
        var gray = ArrayPool<byte>.Shared.Rent(pixels);
        for (int i = 0; i < pixels; i++)
        {
            var srcIdx = i * 4;
            var b = bgra[srcIdx];
            var g = bgra[srcIdx + 1];
            var r = bgra[srcIdx + 2];
            gray[i] = (byte)(0.299f * r + 0.587f * g + 0.114f * b);
        }
        return gray;
    }

    internal static byte[] CropBgraToGray(byte[] bgra, int srcW, int cropX, int cropY,
        int cropW, int cropH)
    {
        var gray = ArrayPool<byte>.Shared.Rent(cropW * cropH);
        var srcRowStride = srcW * 4;

        for (int y = 0; y < cropH; y++)
        {
            var srcOffset = (cropY + y) * srcRowStride + cropX * 4;
            var dstOffset = y * cropW;
            for (int x = 0; x < cropW; x++)
            {
                var i = srcOffset + x * 4;
                var b = bgra[i];
                var g = bgra[i + 1];
                var r = bgra[i + 2];
                gray[dstOffset + x] = (byte)(0.299f * r + 0.587f * g + 0.114f * b);
            }
        }

        return gray;
    }

    internal static byte[] CropAndResizeGray(byte[] srcGray, int srcW, int srcH,
        int cropX, int cropY, int cropW, int cropH, int dstW, int dstH)
    {
        var crop = ArrayPool<byte>.Shared.Rent(cropW * cropH);
        try
        {
            for (int y = 0; y < cropH; y++)
            {
                Array.Copy(srcGray, (cropY + y) * srcW + cropX, crop, y * cropW, cropW);
            }

            return ResizeGray(crop, cropW, cropH, dstW, dstH);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(crop);
        }
    }

    internal static byte[] ResizeGray(byte[] crop, int cropW, int cropH, int dstW, int dstH)
    {
        var maxX = cropW - 1;
        var maxY = cropH - 1;

        var dst = ArrayPool<byte>.Shared.Rent(dstW * dstH);
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

    private static bool VectorFillSupported =>
        Vector128.IsHardwareAccelerated && BitConverter.IsLittleEndian;

    internal static void FillInputTensor(byte[] grayData, float[] destination, int inputSize)
        => FillInputTensor(grayData, destination, inputSize, VectorFillSupported);

    internal static void FillInputTensor(byte[] grayData, float[] destination, int inputSize, bool useVectorPath)
        => FillInputTensor(grayData, destination, inputSize, inputSize, useVectorPath);

    internal static void FillInputTensor(byte[] grayData, float[] destination,
        int inputWidth, int inputHeight, bool useVectorPath)
    {
        var pixels = inputWidth * inputHeight;
        if (grayData.Length < pixels || destination.Length < pixels * 3)
            throw new ArgumentException(
                $"FillInputTensor needs {pixels} source bytes and {pixels * 3} destination floats, " +
                $"got {grayData.Length} and {destination.Length}.");

        int i = 0;

        if (useVectorPath)
        {
            ref byte src = ref MemoryMarshal.GetArrayDataReference(grayData);
            ref float dst = ref MemoryMarshal.GetArrayDataReference(destination);

            var divisor = Vector128.Create(255f);

            for (; i <= pixels - Vector128<float>.Count; i += Vector128<float>.Count)
            {
                var packed = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref src, (nuint)i));
                var widened = Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalar(packed).AsByte()));
                var v = Vector128.ConvertToSingle(widened.AsInt32()) / divisor;
                v.StoreUnsafe(ref dst, (nuint)i);
                v.StoreUnsafe(ref dst, (nuint)(i + pixels));
                v.StoreUnsafe(ref dst, (nuint)(i + 2 * pixels));
            }
        }

        for (; i < pixels; i++)
        {
            var val = grayData[i] / 255f;
            destination[i] = val;
            destination[i + pixels] = val;
            destination[i + 2 * pixels] = val;
        }
    }

    internal static List<DetectionResult> ParseYoloOutputForInput(
        ReadOnlySpan<float> output, int inputWidth, int inputHeight, int numClasses)
    {
        var results = new List<DetectionResult>();
        var numDetections = output.Length / (4 + numClasses);

        for (int i = 0; i < numDetections; i++)
        {
            var classId = 0;
            var maxConf = 0f;
            for (int c = 0; c < numClasses; c++)
            {
                var conf = output[(4 + c) * numDetections + i];
                if (conf > maxConf)
                {
                    maxConf = conf;
                    classId = c;
                }
            }

            if (maxConf < 0.7f) continue;

            var cx = output[i] / inputWidth;
            var cy = output[1 * numDetections + i] / inputHeight;
            var w = output[2 * numDetections + i] / inputWidth;
            var h = output[3 * numDetections + i] / inputHeight;

            results.Add(new DetectionResult
            {
                ClassId = classId,
                Confidence = maxConf,
                X = cx - w / 2,
                Y = cy - h / 2,
                Width = w,
                Height = h,
                Timestamp = DateTime.Now
            });
        }

        if (Log.IsEnabled(LogEventLevel.Debug))
        {
            var highestConf = results.Count > 0
                ? results.Max(r => r.Confidence)
                : 0f;

            if (results.Count == 0 && numDetections > 0)
            {
                for (int i = 0; i < numDetections; i++)
                {
                    for (int c = 0; c < numClasses; c++)
                    {
                        var conf = output[(4 + c) * numDetections + i];
                        if (conf > highestConf) highestConf = conf;
                    }
                }
            }

            var classIds = results.Count > 0
                ? string.Join(",", results.Select(r => $"{r.ClassId}({r.Confidence:F2})"))
                : "none";
            Log.Debug("ParseYoloOutput: {Results} results, highestConf={Conf:F4}, classIds=[{ClassIds}], {Total} detections, numClasses={Classes}",
                results.Count, highestConf, classIds, numDetections, numClasses);
        }

        return results;
    }
}
