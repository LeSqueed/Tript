// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Media;

public static class ClipRegionBounds
{
    public static readonly TimeSpan MinimumDuration = TimeSpan.FromMilliseconds(40);

    private const double MaxRepresentableSeconds = 922_337_203_685.0;

    public static bool TryFromSeconds(double start, double end, out ClipRegion region)
    {
        region = default;

        if (!double.IsFinite(start) || !double.IsFinite(end))
            return false;
        if (Math.Abs(start) > MaxRepresentableSeconds || Math.Abs(end) > MaxRepresentableSeconds)
            return false;

        var (low, high) = start <= end ? (start, end) : (end, start);
        region = new ClipRegion(TimeSpan.FromSeconds(low), TimeSpan.FromSeconds(high));
        return true;
    }

    public static bool TryClamp(ClipRegion region, double sourceDurationSeconds, out ClipRegion clamped)
    {
        var (start, end) = region.Start <= region.End
            ? (region.Start, region.End)
            : (region.End, region.Start);

        if (start < TimeSpan.Zero)
            start = TimeSpan.Zero;
        if (end < TimeSpan.Zero)
            end = TimeSpan.Zero;

        if (double.IsFinite(sourceDurationSeconds)
            && sourceDurationSeconds > 0
            && sourceDurationSeconds <= MaxRepresentableSeconds)
        {
            var limit = TimeSpan.FromSeconds(sourceDurationSeconds);
            if (start > limit)
                start = limit;
            if (end > limit)
                end = limit;
        }

        clamped = new ClipRegion(start, end);
        return clamped.Duration >= MinimumDuration;
    }

    public static IReadOnlyList<ClipRegion> ClampAll(IReadOnlyList<ClipRegion> regions,
        double sourceDurationSeconds)
    {
        ArgumentNullException.ThrowIfNull(regions);

        var kept = new List<ClipRegion>(regions.Count);
        foreach (var region in regions)
        {
            if (TryClamp(region, sourceDurationSeconds, out var clamped))
                kept.Add(clamped);
        }

        return kept;
    }
}
