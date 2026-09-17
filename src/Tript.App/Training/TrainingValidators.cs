// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

#if TRIPT_TRAINING

using Tript.Detection;

namespace Tript.App.Training;

internal static class TrainingLabelValidator
{
    internal static string? FindError(IReadOnlyList<TrainingLabel> labels,
        IReadOnlyList<EventDefinition> definitions, bool requireLabel = true,
        IReadOnlyList<TrainingRegionGroup>? regionGroups = null)
    {
        if (requireLabel && labels.Count == 0)
            return "a sample must contain at least one label";

        var definitionsByClass = definitions
            .Where(definition => definition.DetectionKind == DetectionKind.Object)
            .ToDictionary(definition => definition.ClassId);
        var groups = regionGroups ?? [];
        var regions = TrainingRegionResolver.ResolveByClassId(definitions, groups);
        for (var index = 0; index < labels.Count; index++)
        {
            var label = labels[index];
            if (!definitionsByClass.TryGetValue(label.ClassId, out var definition))
                return $"label {index} uses unknown classId {label.ClassId}";
            if (definition.RegionGroupId is int groupId
                && !groups.Any(group => group.Id == groupId))
            {
                return $"label {index} references a missing region group through '{definition.Name}'";
            }

            if (!double.IsFinite(label.CenterX) || !double.IsFinite(label.CenterY)
                || !double.IsFinite(label.Width) || !double.IsFinite(label.Height))
            {
                return $"label {index} contains a non-finite coordinate";
            }

            if (label.Width <= 0 || label.Height <= 0)
                return $"label {index} has no area";

            var left = label.CenterX - label.Width / 2;
            var top = label.CenterY - label.Height / 2;
            var right = label.CenterX + label.Width / 2;
            var bottom = label.CenterY + label.Height / 2;
            if (left < 0 || top < 0 || right > 1 || bottom > 1)
                return $"label {index} lies outside the image bounds";

            if (regions[label.ClassId] is TrainingScreenRegion region
                && !TrainingRegionResolver.Contains(region, label))
            {
                return $"label {index} lies outside the '{definition.Name}' screen region";
            }
        }

        return null;
    }

    internal static string? FindBlockingError(IReadOnlyList<TrainingLabel> labels,
        IReadOnlyList<EventDefinition> definitions)
    {
        var definitionsByClass = definitions
            .Where(definition => definition.DetectionKind == DetectionKind.Object)
            .ToDictionary(definition => definition.ClassId);
        for (var index = 0; index < labels.Count; index++)
        {
            var label = labels[index];
            if (!definitionsByClass.ContainsKey(label.ClassId))
                return $"label {index} uses unknown classId {label.ClassId}";
            if (!double.IsFinite(label.CenterX) || !double.IsFinite(label.CenterY)
                || !double.IsFinite(label.Width) || !double.IsFinite(label.Height))
            {
                return $"label {index} contains a non-finite coordinate";
            }
            if (label.Width <= 0 || label.Height <= 0)
                return $"label {index} has no area";
            var left = label.CenterX - label.Width / 2;
            var top = label.CenterY - label.Height / 2;
            var right = label.CenterX + label.Width / 2;
            var bottom = label.CenterY + label.Height / 2;
            if (left < 0 || top < 0 || right > 1 || bottom > 1)
                return $"label {index} lies outside the image bounds";
        }
        return null;
    }
}

internal static class TrainingOcrRegionValidator
{
    private const double Tolerance = 1e-4;

    internal static string? FindError(IReadOnlyList<TrainingOcrRegion> regions)
    {
        for (var index = 0; index < regions.Count; index++)
        {
            var region = regions[index];
            if (!double.IsFinite(region.X) || !double.IsFinite(region.Y)
                || !double.IsFinite(region.Width) || !double.IsFinite(region.Height))
            {
                return $"OCR region {index} contains a non-finite coordinate";
            }
            if (region.Width <= 0 || region.Height <= 0)
                return $"OCR region {index} has no area";
            if (region.X < -Tolerance || region.Y < -Tolerance
                || region.X + region.Width > 1 + Tolerance
                || region.Y + region.Height > 1 + Tolerance)
            {
                return $"OCR region {index} lies outside the image bounds";
            }
            if (string.IsNullOrWhiteSpace(region.Text))
                return $"OCR region {index} is empty";
        }
        return null;
    }
}

#endif
