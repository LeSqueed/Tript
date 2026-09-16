// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Settings.Tests;

public sealed class RecordingSessionClockTests
{
    [Fact]
    public void ANewSession_StartsOnTheUtcClock()
    {
        var before = DateTime.UtcNow;
        var session = new RecordingSessionTracker().Start();

        Assert.Equal(DateTimeKind.Utc, session.StartTimeUtc.Kind);
        Assert.InRange(session.StartTimeUtc, before, DateTime.UtcNow);
    }

    [Fact]
    public void ALocalStartTime_IsConvertedToUtc()
    {
        var local = new DateTime(2026, 3, 29, 0, 30, 0, DateTimeKind.Local);

        var session = new RecordingSession(local);

        Assert.Equal(DateTimeKind.Utc, session.StartTimeUtc.Kind);
        Assert.Equal(local.ToUniversalTime(), session.StartTimeUtc);
    }

    [Fact]
    public void AnOffset_IsTheRealElapsedTime_EvenAcrossADaylightSavingChange()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("dst-test", TimeSpan.Zero, "dst-test", "dst-test", "dst-test-summer",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), 3, 29),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 10, 25)),
        ]);
        var startUtc = new DateTime(2026, 3, 29, 0, 30, 0, DateTimeKind.Utc);
        var eventUtc = startUtc.AddMinutes(60);
        var wallClockOffset = TimeZoneInfo.ConvertTimeFromUtc(eventUtc, zone)
            - TimeZoneInfo.ConvertTimeFromUtc(startUtc, zone);

        var session = new RecordingSession(startUtc);

        Assert.Equal(TimeSpan.FromMinutes(120), wallClockOffset);
        Assert.Equal(TimeSpan.FromMinutes(60), eventUtc - session.StartTimeUtc);
    }
}
