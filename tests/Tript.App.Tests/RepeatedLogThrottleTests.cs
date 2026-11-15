// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.App.Tests;

public sealed class RepeatedLogThrottleTests
{
    private long _now;

    private RepeatedLogThrottle Create() => new(() => _now);

    [Fact]
    public void ALineRepeatedInsideTheWindow_IsWrittenOnlyUpToTheLimit()
    {
        var throttle = Create();

        var written = Enumerable.Range(0, 100).Count(_ => throttle.Check("blend state failed").Write);

        Assert.Equal(RepeatedLogThrottle.AllowedPerWindow, written);
    }

    [Fact]
    public void TheLastAllowedRepeat_AnnouncesTheSuppression()
    {
        var throttle = Create();

        var decisions = Enumerable.Range(0, RepeatedLogThrottle.AllowedPerWindow)
            .Select(_ => throttle.Check("blend state failed"))
            .ToList();

        Assert.All(decisions.SkipLast(1), decision => Assert.False(decision.SuppressionStarts));
        Assert.True(decisions[^1].SuppressionStarts);
    }

    [Fact]
    public void AfterTheWindow_TheLineIsWrittenAgainWithTheSuppressedCount()
    {
        var throttle = Create();
        for (var i = 0; i < 50; i++)
            throttle.Check("blend state failed");

        _now += (long)RepeatedLogThrottle.Window.TotalMilliseconds;
        var decision = throttle.Check("blend state failed");

        Assert.True(decision.Write);
        Assert.Equal(50 - RepeatedLogThrottle.AllowedPerWindow, decision.SuppressedBefore);
    }

    [Fact]
    public void DistinctLines_AreLimitedSeparately()
    {
        var throttle = Create();
        for (var i = 0; i < 50; i++)
            throttle.Check("blend state failed");

        Assert.True(throttle.Check("Device Removed Reason: 887A0007").Write);
    }

    [Fact]
    public void WhenTooManyDistinctLinesAreTracked_NewLinesAreStillWritten()
    {
        var throttle = Create();
        for (var i = 0; i < RepeatedLogThrottle.MaxTrackedMessages; i++)
            throttle.Check($"line {i}");

        for (var i = 0; i < 20; i++)
            Assert.True(throttle.Check("untracked line").Write);
    }
}
