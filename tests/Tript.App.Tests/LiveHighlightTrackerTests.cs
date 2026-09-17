// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Core;
using Xunit;

namespace Tript.App.Tests;

public sealed class LiveHighlightTrackerTests
{
    private static readonly TimeSpan Before = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan After = TimeSpan.FromSeconds(8);

    private readonly ManualClock _clock = new();
    private readonly LiveHighlightTracker _tracker;

    public LiveHighlightTrackerTests()
    {
        _tracker = new LiveHighlightTracker(_clock);
    }

    [Fact]
    public void Remember_WhileLiveIsOff_OnlyCollectsTheCandidate()
    {
        _tracker.Begin(_clock.UtcNow, enabled: false);

        Assert.Null(_tracker.Remember(At(10), Before, After));

        var finished = _tracker.Finish();
        Assert.Single(finished.Candidates);
        Assert.False(finished.WereLive);
    }

    [Fact]
    public void Remember_MergesOverlappingBookmarksIntoOneRegion()
    {
        _tracker.Begin(_clock.UtcNow, enabled: true);

        var schedule = _tracker.Remember(At(10), Before, After);
        var merged = _tracker.Remember(At(15), Before, After);
        var separate = _tracker.Remember(At(40), Before, After);

        Assert.NotNull(schedule);
        Assert.Null(merged);
        Assert.NotNull(separate);
        Assert.Equal(TimeSpan.FromSeconds(5), schedule.Region.Start);
        Assert.Equal(TimeSpan.FromSeconds(23), schedule.Region.End);
        Assert.Equal(2, schedule.Region.BookmarkIds.Count);
    }

    [Fact]
    public void TryClaim_WaitsUntilTheRegionAndItsLeadInHavePassed()
    {
        _tracker.Begin(_clock.UtcNow, enabled: true);
        var region = _tracker.Remember(At(10), Before, After)!.Region;

        Assert.Equal(TimeSpan.FromSeconds(24), _tracker.TimeUntilDue(region, Before));
        Assert.Equal(LiveHighlightClaim.NotYet, _tracker.TryClaim(region, Before));

        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(LiveHighlightClaim.Claimed, _tracker.TryClaim(region, Before));
        Assert.Equal(LiveHighlightClaim.Stop, _tracker.TryClaim(region, Before));
        Assert.Null(_tracker.TimeUntilDue(region, Before));

        _tracker.Release([region]);
        Assert.Equal(LiveHighlightClaim.Claimed, _tracker.TryClaim(region, Before));
    }

    [Fact]
    public void Stop_AbandonsInFlightRegions_AndLeavesUnsavedOnesForTheStopReplay()
    {
        _tracker.Begin(_clock.UtcNow, enabled: true);
        var inFlight = _tracker.Remember(At(10), Before, After)!.Region;
        var waiting = _tracker.Remember(At(60), Before, After)!.Region;
        _clock.Advance(TimeSpan.FromSeconds(30));
        _tracker.TryClaim(inFlight, Before);

        _tracker.Stop();

        Assert.True(_tracker.IsAbandoned(inFlight));
        Assert.Equal(LiveHighlightClaim.Stop, _tracker.TryClaim(waiting, Before));
        Assert.Same(waiting, Assert.Single(_tracker.ClaimUnsaved()));
        Assert.Empty(_tracker.ClaimUnsaved());
    }

    [Fact]
    public void Finish_ReportsWhichCandidatesWereSaved_AndResets()
    {
        _tracker.Begin(_clock.UtcNow, enabled: true);
        var saved = At(10);
        var unsaved = At(60);
        var region = _tracker.Remember(saved, Before, After)!.Region;
        _tracker.Remember(unsaved, Before, After);
        _tracker.MarkSaved(region);

        var finished = _tracker.Finish();

        Assert.True(finished.WereLive);
        Assert.Equal(2, finished.Candidates.Count);
        Assert.Contains(saved.Id, finished.SavedBookmarkIds);
        Assert.DoesNotContain(unsaved.Id, finished.SavedBookmarkIds);
        Assert.False(_tracker.EnabledAtSessionStart);
        Assert.Empty(_tracker.Finish().Candidates);
    }

    [Fact]
    public void DisarmSessionStart_ClearsOnlyTheSessionStartFlag()
    {
        _tracker.Begin(_clock.UtcNow, enabled: true);

        _tracker.DisarmSessionStart();

        Assert.False(_tracker.EnabledAtSessionStart);
        Assert.NotNull(_tracker.Remember(At(10), Before, After));
    }

    [Fact]
    public void ElapsedSeconds_CountsFromTheRecordingStart()
    {
        _tracker.Begin(_clock.UtcNow, enabled: false);

        _clock.Advance(TimeSpan.FromSeconds(42));

        Assert.Equal(42, _tracker.ElapsedSeconds, precision: 3);
    }

    private static Bookmark At(int seconds) => new()
    {
        Type = BookmarkType.Kill,
        Time = TimeSpan.FromSeconds(seconds),
    };

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 3, 29, 0, 30, 0, TimeSpan.Zero);

        public DateTime UtcNow => _now.UtcDateTime;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
