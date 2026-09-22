// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Serilog;
using Tript.Core;

namespace Tript.App;

internal sealed class LiveHighlightRegion
{
    internal required TimeSpan Start { get; set; }

    internal required TimeSpan End { get; set; }

    internal HashSet<Guid> BookmarkIds { get; } = [];

    internal bool SaveRequested { get; set; }

    internal bool Abandoned { get; set; }
}

internal sealed record LiveHighlightSchedule(LiveHighlightRegion Region, CancellationToken Token);

internal sealed record FinishedLiveHighlights(
    IReadOnlyList<Bookmark> Candidates,
    IReadOnlySet<Guid> SavedBookmarkIds,
    bool WereLive);

internal enum LiveHighlightClaim
{
    Stop,
    NotYet,
    Claimed,
}

internal sealed class LiveHighlightTracker
{
    private static readonly TimeSpan BoundaryGrace = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly List<Bookmark> _candidates = [];
    private readonly List<Task> _tasks = [];
    private readonly List<LiveHighlightRegion> _regions = [];
    private readonly HashSet<Guid> _savedBookmarkIds = [];
    private CancellationTokenSource? _cancellation;
    private DateTime _startUtc;
    private bool _enabled;
    private bool _enabledAtSessionStart;

    internal LiveHighlightTracker(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    internal DateTime StartUtc
    {
        get
        {
            lock (_gate)
                return _startUtc;
        }
    }

    internal bool EnabledAtSessionStart
    {
        get
        {
            lock (_gate)
                return _enabledAtSessionStart;
        }
    }

    internal double ElapsedSeconds => (UtcNow - StartUtc).TotalSeconds;

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    internal void DisarmSessionStart()
    {
        lock (_gate)
            _enabledAtSessionStart = false;
    }

    internal void Begin(DateTime startedUtc, bool enabled)
    {
        lock (_gate)
        {
            _candidates.Clear();
            _regions.Clear();
            _savedBookmarkIds.Clear();
            _startUtc = startedUtc;
            _enabled = enabled;
            _enabledAtSessionStart = enabled;
            _cancellation?.Dispose();
            _cancellation = enabled ? new CancellationTokenSource() : null;
            _tasks.Clear();
        }
    }

    internal void AddCandidate(Bookmark bookmark)
    {
        lock (_gate)
            _candidates.Add(bookmark);
    }

    internal LiveHighlightSchedule? Remember(Bookmark bookmark, TimeSpan before, TimeSpan after)
    {
        lock (_gate)
        {
            _candidates.Add(bookmark);

            if (!_enabled || _cancellation is null)
                return null;

            var start = bookmark.Time > before
                ? bookmark.Time - before
                : TimeSpan.Zero;
            var existing = _regions.FirstOrDefault(region =>
                !region.SaveRequested && bookmark.Time - before <= region.End);
            if (existing is not null)
            {
                existing.End = existing.End > bookmark.Time + after
                    ? existing.End
                    : bookmark.Time + after;
                existing.BookmarkIds.Add(bookmark.Id);
                return null;
            }

            var region = new LiveHighlightRegion
            {
                Start = start,
                End = bookmark.Time + after,
            };
            region.BookmarkIds.Add(bookmark.Id);
            _regions.Add(region);
            return new LiveHighlightSchedule(region, _cancellation.Token);
        }
    }

    internal void Track(Task task)
    {
        lock (_gate)
        {
            // Stop only needs to wait for tasks still running. Finished ones used to pile up until
            // the next recording began, one per highlight.
            _tasks.RemoveAll(static tracked => tracked.IsCompleted);
            _tasks.Add(task);
        }
    }

    internal TimeSpan? TimeUntilDue(LiveHighlightRegion region, TimeSpan before)
    {
        lock (_gate)
        {
            if (region.SaveRequested || !_enabled)
                return null;
            return DueAt(region, before) - UtcNow;
        }
    }

    internal LiveHighlightClaim TryClaim(LiveHighlightRegion region, TimeSpan before)
    {
        lock (_gate)
        {
            if (region.SaveRequested || !_enabled)
                return LiveHighlightClaim.Stop;
            if (DueAt(region, before) > UtcNow)
                return LiveHighlightClaim.NotYet;
            region.SaveRequested = true;
            return LiveHighlightClaim.Claimed;
        }
    }

    internal void Release(IEnumerable<LiveHighlightRegion> regions)
    {
        lock (_gate)
        {
            foreach (var region in regions)
                region.SaveRequested = false;
        }
    }

    internal void Abandon(IEnumerable<LiveHighlightRegion> regions)
    {
        lock (_gate)
        {
            foreach (var region in regions)
            {
                region.Abandoned = true;
                region.SaveRequested = false;
            }
        }
    }

    internal bool IsAbandoned(LiveHighlightRegion region)
    {
        lock (_gate)
            return region.Abandoned;
    }

    internal void MarkSaved(LiveHighlightRegion region)
    {
        lock (_gate)
        {
            foreach (var bookmarkId in region.BookmarkIds)
                _savedBookmarkIds.Add(bookmarkId);

            // A saved region is inert to everything that walks _regions: Stop skips it and
            // ClaimUnsaved excludes it, both because its bookmarks are now saved. Left in place,
            // Remember scanned every saved region for every new bookmark, which is quadratic over a
            // long session, and a saved region could still be extended by a later bookmark that
            // would then never be saved.
            _regions.Remove(region);
        }
    }

    internal void Stop()
    {
        Task[] tasks;
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            _enabled = false;
            cancellation = _cancellation;
            cancellation?.Cancel();
            _cancellation = null;
            tasks = _tasks.ToArray();
        }

        try
        {
            Task.WaitAll(tasks, StopWait);
        }
        catch (Exception exception) when (exception is AggregateException or ObjectDisposedException)
        {
            Log.Debug(exception, "AppHost: live automatic highlight tasks did not all settle before stop");
        }
        finally
        {
            lock (_gate)
            {
                foreach (var region in _regions)
                {
                    if (region.SaveRequested && !_savedBookmarkIds.Overlaps(region.BookmarkIds))
                    {
                        region.Abandoned = true;
                        region.SaveRequested = false;
                    }
                }
            }
            cancellation?.Dispose();
        }
    }

    internal List<LiveHighlightRegion> ClaimUnsaved()
    {
        lock (_gate)
        {
            var pending = _regions
                .Where(region => !region.Abandoned
                    && !region.SaveRequested
                    && !_savedBookmarkIds.Overlaps(region.BookmarkIds))
                .ToList();
            foreach (var region in pending)
                region.SaveRequested = true;
            return pending;
        }
    }

    internal FinishedLiveHighlights Finish()
    {
        lock (_gate)
        {
            var finished = new FinishedLiveHighlights(
                _candidates.ToList(), _savedBookmarkIds.ToHashSet(), _enabledAtSessionStart);
            _candidates.Clear();
            _regions.Clear();
            _savedBookmarkIds.Clear();
            _enabledAtSessionStart = false;
            _enabled = false;
            return finished;
        }
    }

    private DateTime DueAt(LiveHighlightRegion region, TimeSpan before) =>
        _startUtc + region.End + before + BoundaryGrace;
}
