// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Detection;

internal sealed record OcrRegionBinding(EventDefinition Definition, string SegmentId);

internal sealed class OcrRegionPlan
{
    internal required RegionGroup Region { get; init; }
    internal List<OcrRegionBinding> Bindings { get; } = [];
}

internal static class OcrRegionPlanner
{
    internal static List<OcrRegionPlan> Build(IReadOnlyList<EventDefinition> definitions,
        IReadOnlyList<RegionGroupDefinition>? regionGroups = null)
    {
        var plans = new Dictionary<(float X, float Y, float W, float H), OcrRegionPlan>();
        foreach (var definition in definitions.Where(definition => definition.DetectionKind == DetectionKind.Ocr))
        {
            var (eventX, eventY, eventW, eventH) = EffectiveRegion(definition, regionGroups);
            var segments = definition.Ocr?.Segments.Count > 0
                ? definition.Ocr.Segments
                : [new OcrSegmentDefinition { Id = "default" }];
            foreach (var segment in segments)
            {
                var key = (
                    X: eventX + segment.X * eventW,
                    Y: eventY + segment.Y * eventH,
                    W: segment.Width * eventW,
                    H: segment.Height * eventH);
                if (!plans.TryGetValue(key, out var plan))
                {
                    plan = new OcrRegionPlan
                    {
                        Region = new RegionGroup { X = key.X, Y = key.Y, W = key.W, H = key.H },
                    };
                    plans.Add(key, plan);
                }
                plan.Bindings.Add(new OcrRegionBinding(definition,
                    string.IsNullOrWhiteSpace(segment.Id) ? "default" : segment.Id));
            }
        }
        return plans.Values.ToList();
    }

    // A region group, when the event references one, is the shared screen region the user drew and
    // overrides the event's own per-event region. Falls back to the event's region otherwise.
    private static (float X, float Y, float W, float H) EffectiveRegion(EventDefinition definition,
        IReadOnlyList<RegionGroupDefinition>? regionGroups)
    {
        if (regionGroups is { Count: > 0 } && definition.RegionGroupId is int groupId)
        {
            var group = regionGroups.FirstOrDefault(candidate => candidate.Id == groupId);
            if (group is { ScreenRegionX: var x, ScreenRegionY: var y, ScreenRegionW: var w,
                           ScreenRegionH: var h } && x is float gx && y is float gy
                && w is float gw && h is float gh)
            {
                return (gx, gy, gw, gh);
            }
        }

        return (definition.ScreenRegionX ?? 0, definition.ScreenRegionY ?? 0,
            definition.ScreenRegionW ?? 1, definition.ScreenRegionH ?? 1);
    }
}
