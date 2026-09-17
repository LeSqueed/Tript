// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

#if TRIPT_TRAINING

using Tript.Detection;

namespace Tript.App.Training;

internal static class TrainingLabelSuggestionFilter
{
    private const float OverlapIouThreshold = 0.3f;

    internal static List<TrainingLabelSuggestion> Merge(
        IReadOnlyList<TrainingLabel> existing, IReadOnlyList<DetectionResult> detections,
        IReadOnlyList<EventDefinition> definitions,
        IReadOnlyList<TrainingRegionGroup>? regionGroups = null)
    {
        var regions = TrainingRegionResolver.ResolveByClassId(definitions, regionGroups ?? []);
        var definitionsByClass = definitions
            .Where(definition => definition.DetectionKind == DetectionKind.Object)
            .ToDictionary(definition => definition.ClassId);
        var accepted = existing.Select(ToBox).ToList();
        var suggestions = new List<TrainingLabelSuggestion>();
        foreach (var detection in detections.OrderByDescending(detection => detection.Confidence))
        {
            if (!float.IsFinite(detection.X) || !float.IsFinite(detection.Y)
                || !float.IsFinite(detection.Width) || !float.IsFinite(detection.Height)
                || detection.Width <= 0 || detection.Height <= 0
                || detection.X < 0 || detection.Y < 0
                || detection.X + detection.Width > 1 || detection.Y + detection.Height > 1)
                continue;

            if (!definitionsByClass.TryGetValue(detection.ClassId, out var definition))
                continue;

            TrainingLabel label;
            if (definition.FixedPosition && definition.FixedLabelCenterX is not null
                && definition.FixedLabelCenterY is not null && definition.FixedLabelWidth is not null
                && definition.FixedLabelHeight is not null)
            {
                label = new TrainingLabel
                {
                    ClassId = detection.ClassId,
                    CenterX = definition.FixedLabelCenterX.Value,
                    CenterY = definition.FixedLabelCenterY.Value,
                    Width = definition.FixedLabelWidth.Value,
                    Height = definition.FixedLabelHeight.Value,
                };
            }
            else if (definition.FixedPosition)
            {
                continue;
            }
            else
            {
                label = new TrainingLabel
                {
                    ClassId = detection.ClassId,
                    CenterX = detection.X + detection.Width / 2,
                    CenterY = detection.Y + detection.Height / 2,
                    Width = detection.Width,
                    Height = detection.Height,
                };
            }

            regions.TryGetValue(detection.ClassId, out var region);
            if (region is not null && !TrainingRegionResolver.Contains(region.Value, label))
                continue;

            var box = ToBox(label);
            if (accepted.Any(existingBox => IoU(existingBox, box) >= OverlapIouThreshold))
                continue;

            accepted.Add(box);
            suggestions.Add(new TrainingLabelSuggestion
            {
                Label = label,
                Confidence = detection.Confidence,
            });
        }

        return suggestions;
    }

    private static Box ToBox(TrainingLabel label) => new(
        (float)(label.CenterX - label.Width / 2),
        (float)(label.CenterY - label.Height / 2),
        (float)label.Width,
        (float)label.Height);

    private static Box ToBox(DetectionResult detection) => new(
        detection.X, detection.Y, detection.Width, detection.Height);

    private static float IoU(Box left, Box right)
    {
        var leftArea = left.Width * left.Height;
        var rightArea = right.Width * right.Height;
        if (leftArea <= 0 || rightArea <= 0) return 1f;

        var overlapWidth = MathF.Min(left.X + left.Width, right.X + right.Width)
            - MathF.Max(left.X, right.X);
        var overlapHeight = MathF.Min(left.Y + left.Height, right.Y + right.Height)
            - MathF.Max(left.Y, right.Y);
        if (overlapWidth <= 0 || overlapHeight <= 0) return 0;

        var intersection = overlapWidth * overlapHeight;
        return intersection / (leftArea + rightArea - intersection);
    }

    private readonly record struct Box(float X, float Y, float Width, float Height);
}

#endif
