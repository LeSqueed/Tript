// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Media;

// Converts eligible event times into non-overlapping pre/post-roll regions. Keeping this separate
// from recording and ffmpeg makes the chain rule deterministic and testable without media binaries.
public static class AutomaticClipPlanner
{
    public static readonly TimeSpan PreRoll = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan PostRoll = TimeSpan.FromSeconds(10);

    public static IReadOnlyList<ClipRegion> Plan(IEnumerable<TimeSpan> bookmarkTimes)
    {
        ArgumentNullException.ThrowIfNull(bookmarkTimes);

        var candidates = bookmarkTimes
            .Where(time => time >= TimeSpan.Zero)
            .OrderBy(time => time)
            .ToList();

        if (candidates.Count == 0)
            return [];

        var merged = new List<ClipRegion>(candidates.Count);
        var current = RegionFor(candidates[0]);
        foreach (var candidate in candidates.Skip(1))
        {
            // Merge whenever the candidate's pre-roll reaches the current region.
            if (RegionFor(candidate).Start <= current.End)
            {
                current = current with { End = candidate + PostRoll };
                continue;
            }

            merged.Add(current);
            current = RegionFor(candidate);
        }

        merged.Add(current);
        return merged;
    }

    private static ClipRegion RegionFor(TimeSpan trigger) => new(
        trigger > PreRoll ? trigger - PreRoll : TimeSpan.Zero,
        trigger + PostRoll);
}
