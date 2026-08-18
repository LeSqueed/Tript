// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Media;
using Xunit;

namespace Tript.Media.Tests;

// The backend's own gate on clip region bounds. These tests need neither ffmpeg nor the IPC
// surface: the whole point of putting the clamping in ClipRegionBounds is that the rules are
// checkable without either, because the rules are what stand between a hostile or stale CreateClip
// and an ffmpeg invocation that reports success on an empty file.
public class ClipRegionBoundsTests
{
    // ---- TryFromSeconds: the double -> TimeSpan boundary ----

    [Theory]
    [InlineData(double.NaN, 1.0)]
    [InlineData(0.0, double.NaN)]
    [InlineData(double.NaN, double.NaN)]
    public void TryFromSeconds_refuses_not_a_number(double start, double end)
    {
        // A raw TimeSpan.FromSeconds(NaN) throws ArgumentException, on the IPC dispatch thread, where
        // the only handler writes the message to stderr — the frontend gets no frame at all. Refusing
        // is the behaviour that can be reported.
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
        // Not an exotic input: 1e18 is an unremarkable JSON number and TimeSpan.FromSeconds overflows
        // on it (TimeSpan.MaxValue.TotalSeconds is 922337203685.4775, measured). So the bound is on
        // magnitude, not only on finiteness.
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
        // A swapped pair names exactly one interval, so normalising it loses nothing. Refusing would
        // raise an error whose only remedy is "draw the same region the other way round".
        Assert.True(ClipRegionBounds.TryFromSeconds(9.5, 1.25, out var region));
        Assert.Equal(TimeSpan.FromSeconds(1.25), region.Start);
        Assert.Equal(TimeSpan.FromSeconds(9.5), region.End);
    }

    [Fact]
    public void TryFromSeconds_accepts_negative_times_and_leaves_the_clamp_to_the_duration_pass()
    {
        // Negative is representable, so it is not the conversion's business to reject it; it is
        // clamped to zero once a duration is known. Keeping the two concerns apart is what lets this
        // layer run before MediaProbe has said anything.
        Assert.True(ClipRegionBounds.TryFromSeconds(-5.0, 2.0, out var region));
        Assert.Equal(TimeSpan.FromSeconds(-5.0), region.Start);
    }

    // ---- TryClamp: fitting a region to the source's real length ----

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
        // ffmpeg produces exactly this clip on its own for a straddling region (-ss 1.5 -t 3 on a 2 s
        // file gives a correct 0.5 s output, measured). Clamping here is what makes the request
        // succeed rather than be refused for naming a time past the end.
        Assert.True(ClipRegionBounds.TryClamp(ClipRegion.FromSeconds(1.5, 4.5), 2.0, out var clamped));
        Assert.Equal(TimeSpan.FromSeconds(1.5), clamped.Start);
        Assert.Equal(TimeSpan.FromSeconds(2.0), clamped.End);
    }

    [Fact]
    public void TryClamp_drops_a_region_wholly_past_the_end()
    {
        // Both endpoints clamp to the duration, so nothing is left. Handed to ffmpeg unclamped this
        // is the worst case: exit code 0, empty stderr, and a 261-byte MP4 with no video stream.
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
        // Not a cosmetic rule. ffmpeg reads "-t 0" as "no limit", so a zero-length region does not
        // yield an empty clip, it yields the entire remaining recording (measured).
        Assert.False(ClipRegionBounds.TryClamp(ClipRegion.FromSeconds(1.0, 1.0), 10, out _));
    }

    [Fact]
    public void TryClamp_drops_a_region_shorter_than_the_minimum()
    {
        var tooShort = ClipRegion.FromSeconds(1.0, 1.0 + ClipRegionBounds.MinimumDuration.TotalSeconds / 2);
        Assert.False(ClipRegionBounds.TryClamp(tooShort, 10, out _));

        // And the boundary itself is kept, so the rule is a floor rather than an off-by-one.
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
        // MediaProbe reports DurationSeconds as NaN when the container carries no parseable duration.
        // An unknown upper bound cannot be enforced, but the ordering, the non-negativity and the
        // minimum length can be — and the old check could not even do that: it compared against
        // TimeSpan.FromSeconds(NaN), which throws ArgumentException from inside the comparison.
        Assert.True(ClipRegionBounds.TryClamp(ClipRegion.FromSeconds(9.0, -1.0), duration, out var clamped));
        Assert.Equal(TimeSpan.Zero, clamped.Start);
        Assert.Equal(TimeSpan.FromSeconds(9.0), clamped.End);

        Assert.False(ClipRegionBounds.TryClamp(ClipRegion.FromSeconds(3.0, 3.0), duration, out _));
    }

    // ---- ClampAll: the whole request ----

    [Fact]
    public void ClampAll_keeps_the_survivors_in_timeline_order_and_drops_the_rest()
    {
        var regions = new[]
        {
            ClipRegion.FromSeconds(-3, 1.0),   // clamps to 0 - 1.0
            ClipRegion.FromSeconds(5, 8),      // wholly past the end: dropped
            ClipRegion.FromSeconds(1.5, 4.5),  // straddles the end: clamps to 1.5 - 2.0
            ClipRegion.FromSeconds(1.9, 1.9),  // zero length: dropped
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
        // The caller's signal to fail the request rather than run ffmpeg. Every out-of-bounds region
        // ffmpeg is actually handed comes back as exit code 0, so "run it and see" reports success.
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

    // ---- The invariant the engine relies on ----

    [Fact]
    public void Every_surviving_region_is_inside_the_recording_and_non_degenerate()
    {
        // The property the ffmpeg argument builder is entitled to assume, over a spread of hostile
        // and merely wrong inputs: 0 <= Start < End <= duration, for every duration the probe can
        // plausibly report. Anything that cannot satisfy it is absent from the result, never repaired
        // into something that violates it.
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
                    // Built through the wire conversion, so the pair is ordered but otherwise
                    // untouched — including the pairs that arrive swapped.
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
