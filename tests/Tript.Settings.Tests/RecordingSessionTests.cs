// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Tript.Core;
using Xunit;

namespace Tript.Settings.Tests;

public class RecordingSessionTests
{
    private static readonly DateTime Origin = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AddBookmark_StoresBookmarkWithOffsetFromStart()
    {
        var session = new RecordingSession(Origin);

        var bookmark = new Bookmark
        {
            Type = BookmarkType.Kill,
            Time = TimeSpan.FromSeconds(30),
        };
        session.AddBookmark(bookmark);

        var stored = Assert.Single(session.Bookmarks);
        Assert.Equal(BookmarkType.Kill, stored.Type);
        Assert.Equal(TimeSpan.FromSeconds(30), stored.Time);
        Assert.Equal(Origin, session.StartTimeUtc);
    }

    [Fact]
    public void RemoveBookmark_TakesOutTheMatchingIdOnly()
    {
        var session = new RecordingSession(Origin);
        var kept = new Bookmark { Type = BookmarkType.Kill, Time = TimeSpan.FromSeconds(10) };
        var removed = new Bookmark { Type = BookmarkType.Manual, Time = TimeSpan.FromSeconds(20) };
        session.AddBookmark(kept);
        session.AddBookmark(removed);

        Assert.True(session.RemoveBookmark(removed.Id));

        Assert.Equal(kept.Id, Assert.Single(session.Bookmarks).Id);
    }

    [Fact]
    public void RemoveBookmark_ReportsAnUnknownIdRatherThanThrowing()
    {
        var session = new RecordingSession(Origin);
        session.AddBookmark(new Bookmark { Type = BookmarkType.Kill, Time = TimeSpan.FromSeconds(10) });

        Assert.False(session.RemoveBookmark(Guid.NewGuid()));

        Assert.Single(session.Bookmarks);
    }

    // The seam the detector writes into: detection runs on its own inference thread, so adding
    // bookmarks concurrently must never lose one or corrupt the list. The detector computes the
    // offset itself from the session start time, so this test exercises the concurrent writes
    // against the same session.
    [Fact]
    public void AddBookmark_ConcurrentWriters_AllLand()
    {
        var session = new RecordingSession(Origin);
        const int threads = 4;
        const int perThread = 500;

        var workers = new List<Thread>();
        for (int t = 0; t < threads; t++)
        {
            var offset = t;
            var worker = new Thread(() =>
            {
                for (int i = 0; i < perThread; i++)
                {
                    session.AddBookmark(new Bookmark
                    {
                        Type = BookmarkType.Manual,
                        Time = TimeSpan.FromMilliseconds(offset * 1000 + i),
                    });
                }
            });
            workers.Add(worker);
            worker.Start();
        }

        foreach (var worker in workers)
            Assert.True(worker.Join(TimeSpan.FromSeconds(10)), "a bookmark writer did not finish");

        Assert.Equal(threads * perThread, session.Bookmarks.Count);
    }

    [Fact]
    public void AddBookmark_SnapshotIsConsistentUnderConcurrentWrites()
    {
        var session = new RecordingSession(Origin);
        var stop = new ManualResetEventSlim();

        var writer = new Thread(() =>
        {
            for (int i = 0; !stop.IsSet && i < 100_000; i++)
                session.AddBookmark(new Bookmark { Type = BookmarkType.Manual });
        })
        { IsBackground = true };

        writer.Start();

        // The snapshot must be a consistent, complete list of whatever was written by the moment
        // it was taken — never a torn view, never duplicates.
        for (int i = 0; i < 500 && !stop.IsSet; i++)
        {
            var snapshot = session.Bookmarks;
            Assert.Equal(snapshot.Count, snapshot.Distinct().Count());
        }

        stop.Set();
        Assert.True(writer.Join(TimeSpan.FromSeconds(10)), "bookmark writer did not stop");
    }

    // The tracker owns the process-wide active-session state and registers the resolver with the
    // core registry. This test pins the full seam: the registry hands the active session to
    // whatever asks, per call, and Stop clears it.
    [Fact]
    public void Tracker_RegistersWithTheCoreRegistry()
    {
        var tracker = new RecordingSessionTracker();
        tracker.Register();
        try
        {
            Assert.Null(RecordingSessionRegistry.Active);

            var session = tracker.Start(Origin);
            Assert.Same(session, RecordingSessionRegistry.Active);

            var stopped = tracker.Stop();
            Assert.Same(session, stopped);
            Assert.Null(RecordingSessionRegistry.Active);
        }
        finally
        {
            RecordingSessionRegistry.Reset();
        }
    }
}
