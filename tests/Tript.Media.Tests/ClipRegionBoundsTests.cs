// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Media;
using Xunit;

namespace Tript.Media.Tests;

public class ClipRegionBoundsTests
{
    [Theory]
    [InlineData(double.NaN, 1.0)]
    [InlineData(0.0, double.NaN)]
    [InlineData(double.NaN, double.NaN)]
    public void TryFromSeconds_refuses_not_a_number(double start, double end)
    {
        Assert.False(ClipRegionBounds.TryFromSeconds(start, end, out _));
    }

    [Theory]
    [InlineData(double.PositiveInfinity, 1.0)]
    [InlineData(double.NegativeInfinity, 1.0)]
    [InlineData(0.0, double.PositiveInfinity)]
    [InlineData(0.0, double.NegativeInfinity)]
    [InlineData(double.NegativeInfinity, double.PositiveInfinity)]
    public void TryFromSeconds_refuses_infinities(double start, double end)
    {
        Assert.False(ClipRegionBounds.TryFromSeconds(start, end, out _));
    }

    [Theory]
    [InlineData(0.0, 1e18)]
    [InlineData(0.0, 1e308)]
    [InlineData(-1e308, 0.0)]
    [InlineData(0.0, 1e12)]
    public void TryFromSeconds_refuses_values_TimeSpan_cannot_hold(double start, double end)
    {
        Assert.False(ClipRegionBounds.TryFromSeconds(start, end, out _));
    }

    [Fact]
    public void TryFromSeconds_passes_ordinary_times_through_unchanged()
    {
        Assert.True(ClipRegionBounds.TryFromSeconds(1.25, 9.5, out var region));
        Assert.Equal(TimeSpan.FromSeconds(1.25), region.Start);
        Assert.Equal(TimeSpan.FromSeconds(9.5), region.End);
    }

    [Fact]
    public void TryFromSeconds_orders_a_swapped_pair_instead_of_refusing_it()
    {
        Assert.True(ClipRegionBounds.TryFromSeconds(9.5, 1.25, out var region));
        Assert.Equal(TimeSpan.FromSeconds(1.25), region.Start);
        Assert.Equal(TimeSpan.FromSeconds(9.5), region.End);
    }

    [Fact]
    public void TryFromSeconds_accepts_negative_times_and_leaves_the_clamp_to_the_duration_pass()
    {
        Assert.True(ClipRegionBounds.TryFromSeconds(-5.0, 2.0, out var region));
        Assert.Equal(TimeSpan.FromSeconds(-5.0), region.Start);
    }

    [Fact]
    public void TryClamp_pulls_a_negative_start_up_to_zero()
    {
        Assert.True(ClipRegionBounds.TryClamp(ClipRegion.FromSeconds(-5, 2), 10, out var clamped));
        Assert.Equal(TimeSpan.Zero, clamped.Start);
        Assert.Equal(TimeSpan.FromSeconds(2), clamped.End);
    }

    [Fact]
    public void TryClamp_truncates_a_region_straddling_the_end_at_the_duration()
    {
        Assert.True(ClipRegionBounds.TryClamp(ClipRegion.FromSeconds(1.5, 4.5), 2.0, out var clamped));
        Assert.Equal(TimeSpan.FromSeconds(1.5), clamped.Start);
        Assert.Equal(TimeSpan.FromSeconds(2.0), clamped.End);
    }

    [Fact]
    public void TryClamp_drops_a_region_wholly_past_the_end()
    {
        Assert.False(ClipRegionBounds.TryClamp(ClipRegion.FromSeconds(5, 8), 2.0, out _));
    }

    [Fact]
    public void TryClamp_drops_a_region_wholly_before_the_start()
    {
        Assert.False(ClipRegionBounds.TryClamp(ClipRegion.FromSeconds(-8, -5), 2.0, out _));
    }

    [Fact]
    public void TryClamp_drops_a_zero_length_region()
    {
        Assert.False(ClipRegionBounds.TryClamp(ClipRegion.FromSeconds(1.0, 1.0), 10, out _));
    }

    [Fact]
    public void TryClamp_drops_a_region_shorter_than_the_minimum()
    {
        var tooShort = ClipRegion.FromSeconds(1.0, 1.0 + ClipRegionBounds.MinimumDuration.TotalSeconds / 2);
        Assert.False(ClipRegionBounds.TryClamp(tooShort, 10, out _));

        var exactlyMinimum = ClipRegion.FromSeconds(1.0, 1.0 + ClipRegionBounds.MinimumDuration.TotalSeconds);
        Assert.True(ClipRegionBounds.TryClamp(exactlyMinimum, 10, out _));
    }

    [Fact]
    public void TryClamp_keeps_a_region_that_ends_exactly_at_the_duration()
    {
        Assert.True(ClipRegionBounds.TryClamp(ClipRegion.FromSeconds(0, 2.0), 2.0, out var clamped));
        Assert.Equal(TimeSpan.FromSeconds(2.0), clamped.End);
    }

    [Fact]
    public void TryClamp_orders_a_swapped_region()
    {
        Assert.True(ClipRegionBounds.TryClamp(ClipRegion.FromSeconds(4.0, 1.0), 10, out var clamped));
        Assert.Equal(TimeSpan.FromSeconds(1.0), clamped.Start);
        Assert.Equal(TimeSpan.FromSeconds(4.0), clamped.End);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.PositiveInfinity)]
    public void TryClamp_with_an_unknown_duration_still_enforces_everything_it_can(double duration)
    {
        Assert.True(ClipRegionBounds.TryClamp(ClipRegion.FromSeconds(9.0, -1.0), duration, out var clamped));
        Assert.Equal(TimeSpan.Zero, clamped.Start);
        Assert.Equal(TimeSpan.FromSeconds(9.0), clamped.End);

        Assert.False(ClipRegionBounds.TryClamp(ClipRegion.FromSeconds(3.0, 3.0), duration, out _));
    }

    [Fact]
    public void ClampAll_keeps_the_survivors_in_timeline_order_and_drops_the_rest()
    {
        var regions = new[]
        {
            ClipRegion.FromSeconds(-3, 1.0),
            ClipRegion.FromSeconds(5, 8),
            ClipRegion.FromSeconds(1.5, 4.5),
            ClipRegion.FromSeconds(1.9, 1.9),
        };

        var kept = ClipRegionBounds.ClampAll(regions, 2.0);

        Assert.Equal(2, kept.Count);
        Assert.Equal(TimeSpan.Zero, kept[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(1.0), kept[0].End);
        Assert.Equal(TimeSpan.FromSeconds(1.5), kept[1].Start);
        Assert.Equal(TimeSpan.FromSeconds(2.0), kept[1].End);
    }

    [Fact]
    public void ClampAll_returns_nothing_when_no_region_survives()
    {
        var regions = new[]
        {
            ClipRegion.FromSeconds(120, 130),
            ClipRegion.FromSeconds(-9, -4),
            ClipRegion.FromSeconds(1, 1),
        };

        Assert.Empty(ClipRegionBounds.ClampAll(regions, 2.0));
    }

    [Fact]
    public void ClampAll_of_no_regions_is_no_regions()
    {
        Assert.Empty(ClipRegionBounds.ClampAll([], 2.0));
    }

    [Fact]
    public void Every_surviving_region_is_inside_the_recording_and_non_degenerate()
    {
        double[] durations = [0.04, 0.5, 2.0, 7.25, 3600.0];
        double[] times =
        [
            -1e6, -3600, -7.25, -0.04, -1e-9, 0, 1e-9, 0.02, 0.04, 0.5, 1.0, 1.9999, 2.0, 2.0001,
            7.25, 120, 3600, 86_400, 1e9,
        ];

        foreach (var duration in durations)
        {
            var limit = TimeSpan.FromSeconds(duration);
            var candidates = new List<ClipRegion>();
            foreach (var start in times)
            {
                foreach (var end in times)
                {
                    Assert.True(ClipRegionBounds.TryFromSeconds(start, end, out var region),
                        $"({start}, {end}) is representable and must convert.");
                    candidates.Add(region);
                }
            }

            foreach (var kept in ClipRegionBounds.ClampAll(candidates, duration))
            {
                Assert.True(kept.Start >= TimeSpan.Zero, $"Start {kept.Start} is before the recording.");
                Assert.True(kept.Start < kept.End, $"Start {kept.Start} is not before end {kept.End}.");
                Assert.True(kept.End <= limit, $"End {kept.End} is past the duration {limit}.");
                Assert.True(kept.Duration >= ClipRegionBounds.MinimumDuration,
                    $"Duration {kept.Duration} is below the minimum.");
            }
        }
    }
}
