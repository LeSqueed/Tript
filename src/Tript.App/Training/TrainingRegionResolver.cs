// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

#if TRIPT_TRAINING

using System.Text.Json;
using Tript.Detection;

namespace Tript.App.Training;

internal sealed class TrainingRegionGroup
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public float? ScreenRegionX { get; set; }
    public float? ScreenRegionY { get; set; }
    public float? ScreenRegionW { get; set; }
    public float? ScreenRegionH { get; set; }
}

internal readonly record struct TrainingScreenRegion(double X, double Y, double Width, double Height)
{
    internal double Right => X + Width;
    internal double Bottom => Y + Height;
}

internal static class TrainingRegionResolver
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal static readonly JsonSerializerOptions WriteJsonOptions = new(JsonOptions)
    {
        WriteIndented = true,
    };

    internal static TrainingScreenRegion? EffectiveRegion(EventDefinition definition,
        IReadOnlyList<TrainingRegionGroup> groups)
    {
        if (definition.RegionGroupId is int groupId)
        {
            var group = groups.FirstOrDefault(candidate => candidate.Id == groupId);
            if (group is not null)
                return ToRegion(group.ScreenRegionX, group.ScreenRegionY,
                    group.ScreenRegionW, group.ScreenRegionH);
        }

        return ToRegion(definition.ScreenRegionX, definition.ScreenRegionY,
            definition.ScreenRegionW, definition.ScreenRegionH);
    }

    internal static Dictionary<int, TrainingScreenRegion?> ResolveByClassId(
        IReadOnlyList<EventDefinition> definitions, IReadOnlyList<TrainingRegionGroup> groups) =>
        definitions.Where(definition => definition.DetectionKind == DetectionKind.Object)
            .ToDictionary(definition => definition.ClassId,
            definition => EffectiveRegion(definition, groups));

    internal static List<EventDefinition> MaterializeEffectiveRegions(
        IReadOnlyList<EventDefinition> definitions, IReadOnlyList<TrainingRegionGroup> groups) =>
        definitions.Select(definition =>
        {
            var clone = new EventDefinition
            {
                Id = definition.Id,
                Name = definition.Name,
                Type = definition.Type,
                DetectionKind = definition.DetectionKind,
                ClassId = definition.ClassId,
                Ocr = definition.Ocr,
                SubtractsEventId = definition.SubtractsEventId,
                BookmarkType = definition.BookmarkType,
                IncludeInAutoClips = definition.IncludeInAutoClips,
                RegionGroupId = definition.RegionGroupId,
                FixedPosition = definition.FixedPosition,
                FixedLabelCenterX = definition.FixedLabelCenterX,
                FixedLabelCenterY = definition.FixedLabelCenterY,
                FixedLabelWidth = definition.FixedLabelWidth,
                FixedLabelHeight = definition.FixedLabelHeight,
            };
            ApplyRegion(clone, EffectiveRegion(definition, groups));
            return clone;
        }).ToList();

    internal static bool Contains(TrainingScreenRegion region, TrainingLabel label)
    {
        const double epsilon = 0.000001;
        var left = label.CenterX - label.Width / 2;
        var top = label.CenterY - label.Height / 2;
        var right = label.CenterX + label.Width / 2;
        var bottom = label.CenterY + label.Height / 2;
        return left + epsilon >= region.X && top + epsilon >= region.Y
            && right - epsilon <= region.Right && bottom - epsilon <= region.Bottom;
    }

    internal static void ApplyRegion(EventDefinition definition, TrainingScreenRegion? region)
    {
        definition.ScreenRegionX = region is null ? null : (float)region.Value.X;
        definition.ScreenRegionY = region is null ? null : (float)region.Value.Y;
        definition.ScreenRegionW = region is null ? null : (float)region.Value.Width;
        definition.ScreenRegionH = region is null ? null : (float)region.Value.Height;
    }

    private static TrainingScreenRegion? ToRegion(float? x, float? y, float? width, float? height)
    {
        if (x is not float regionX || y is not float regionY
            || width is not float regionWidth || height is not float regionHeight)
            return null;
        return new TrainingScreenRegion(regionX, regionY, regionWidth, regionHeight);
    }
}

#endif
