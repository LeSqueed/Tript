// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Media;
using Xunit;

namespace Tript.Media.Tests;

public sealed class AutomaticClipPlannerTests
{
    [Fact]
    public void Plan_AddsTenSecondsBeforeAndAfter()
    {
        var regions = AutomaticClipPlanner.Plan([TimeSpan.FromSeconds(30)]);

        var region = Assert.Single(regions);
        Assert.Equal(TimeSpan.FromSeconds(20), region.Start);
        Assert.Equal(TimeSpan.FromSeconds(40), region.End);
    }

    [Fact]
    public void Plan_MergesTriggersWithinTenSeconds()
    {
        var regions = AutomaticClipPlanner.Plan([
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(40),
            TimeSpan.FromSeconds(100),
        ]);

        Assert.Equal(
            [
                new ClipRegion(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(50)),
                new ClipRegion(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(110)),
            ],
            regions);
    }

    [Fact]
    public void Plan_DoesNotMergeTriggersMoreThanTenSecondsApartEvenWhenWindowsOverlap()
    {
        var regions = AutomaticClipPlanner.Plan([
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(45),
        ]);

        Assert.Equal(
            [
                new ClipRegion(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40)),
                new ClipRegion(TimeSpan.FromSeconds(35), TimeSpan.FromSeconds(55)),
            ],
            regions);
    }

    [Fact]
    public void Plan_ClampsPreRollAtSessionStart()
    {
        var regions = AutomaticClipPlanner.Plan([TimeSpan.FromSeconds(4)]);

        var region = Assert.Single(regions);
        Assert.Equal(TimeSpan.Zero, region.Start);
        Assert.Equal(TimeSpan.FromSeconds(14), region.End);
    }

    [Fact]
    public void Plan_IgnoresNegativeTimes()
    {
        Assert.Empty(AutomaticClipPlanner.Plan([TimeSpan.FromSeconds(-1)]));
    }
}
