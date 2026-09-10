// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Detection;

public static class DetectionBatchCounter
{
    private const float OverlapIouThreshold = 0.3f;

    public static IReadOnlyList<DetectionResult> DistinctDetections(IReadOnlyList<DetectionResult> results)
    {
        var instances = new List<DetectionResult>();
        foreach (var result in results
            .OrderByDescending(result => result.Confidence)
            .ThenBy(result => result.X)
            .ThenBy(result => result.Y)
            .ThenBy(result => result.Width)
            .ThenBy(result => result.Height))
        {
            var bestIndex = -1;
            var bestIou = 0f;
            for (var index = 0; index < instances.Count; index++)
            {
                var iou = IntersectionOverUnion(instances[index], result);
                if (iou >= OverlapIouThreshold && iou > bestIou)
                {
                    bestIndex = index;
                    bestIou = iou;
                }
            }

            if (bestIndex >= 0)
                instances[bestIndex] = result;
            else
                instances.Add(result);
        }

        return instances;
    }

    private static float IntersectionOverUnion(DetectionResult leftBox, DetectionResult rightBox)
    {
        var leftArea = leftBox.Width * leftBox.Height;
        var rightArea = rightBox.Width * rightBox.Height;
        if (leftArea <= 0 || rightArea <= 0) return 1f;

        var left = MathF.Max(leftBox.X, rightBox.X);
        var top = MathF.Max(leftBox.Y, rightBox.Y);
        var right = MathF.Min(leftBox.X + leftBox.Width, rightBox.X + rightBox.Width);
        var bottom = MathF.Min(leftBox.Y + leftBox.Height, rightBox.Y + rightBox.Height);
        var overlapWidth = right - left;
        var overlapHeight = bottom - top;
        if (overlapWidth <= 0 || overlapHeight <= 0) return 0f;

        var intersection = overlapWidth * overlapHeight;
        return intersection / (leftArea + rightArea - intersection);
    }
}
