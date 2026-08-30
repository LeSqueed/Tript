// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Media;
using Xunit;

namespace Tript.Media.Tests;

public sealed class AutomaticClipPlannerTests
{
    [Fact]
    public void Plan_AddsFiveSecondsBeforeAndEightAfter()
    {
        var regions = AutomaticClipPlanner.Plan(
            [TimeSpan.FromSeconds(30)], TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(8));

        var region = Assert.Single(regions);
        Assert.Equal(TimeSpan.FromSeconds(25), region.Start);
        Assert.Equal(TimeSpan.FromSeconds(38), region.End);
    }

    [Fact]
    public void Plan_MergesTriggersWhosePreRollsReach()
    {
        var regions = AutomaticClipPlanner.Plan(
            [
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(40),
                TimeSpan.FromSeconds(100),
            ],
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(8));

        Assert.Equal(
            [
                new ClipRegion(TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(48)),
                new ClipRegion(TimeSpan.FromSeconds(95), TimeSpan.FromSeconds(108)),
            ],
            regions);
    }

    [Fact]
    public void Plan_MergesTriggersWhenTheirWindowsOverlap()
    {
        var regions = AutomaticClipPlanner.Plan(
            [
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(45),
            ],
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(8));

        Assert.Equal(
            [
                new ClipRegion(TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(38)),
                new ClipRegion(TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(53)),
            ],
            regions);
    }

    [Fact]
    public void Plan_ClampsPreRollAtSessionStart()
    {
        var regions = AutomaticClipPlanner.Plan(
            [TimeSpan.FromSeconds(4)], TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(8));

        var region = Assert.Single(regions);
        Assert.Equal(TimeSpan.Zero, region.Start);
        Assert.Equal(TimeSpan.FromSeconds(12), region.End);
    }

    [Fact]
    public void Plan_IgnoresNegativeTimes()
    {
        Assert.Empty(
            AutomaticClipPlanner.Plan(
                [TimeSpan.FromSeconds(-1)], TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(8)));
    }
}
