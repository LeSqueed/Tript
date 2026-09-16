// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

// The concrete recording session the detector writes bookmarks into, backing the seam in
// Tript.Core (IRecordingSession). Bookmark times are stored as offsets from the session start,
// so the start time is fixed when the session begins and never changes while it is live.
using Tript.Core;

namespace Tript.Settings;

public sealed class RecordingSession : IRecordingSession
{
    private readonly object _gate = new();

    private readonly List<Bookmark> _bookmarks = [];

    // The wall-clock time the recording began; bookmarks are stored as offsets from it. Fixed at
    // construction so a bookmark written mid-session always resolves against the same origin.
    public DateTime StartTime { get; }

    // The bookmarks written so far, in write order. The snapshot is the contract for anything
    // that consumes the session after it stops (metadata serialization, the timeline); live
    // consumers take the snapshot at the moment they need a consistent view.
    public IReadOnlyList<Bookmark> Bookmarks
    {
        get
        {
            lock (_gate)
            {
                return _bookmarks.ToArray();
            }
        }
    }

    public RecordingSession(DateTime startTime)
    {
        StartTime = startTime;
    }

    // Called from whichever thread noticed the event — including a detector's own inference
    // thread — so the gate is the only mutable state. Identity, not offset, decides what is new:
    // a bookmark is a distinct event even when it lands at the same offset as another.
    public void AddBookmark(Bookmark bookmark)
    {
        ArgumentNullException.ThrowIfNull(bookmark);

        lock (_gate)
        {
            _bookmarks.Add(bookmark);
        }
    }

    public bool RemoveBookmark(Guid id)
    {
        lock (_gate)
        {
            return _bookmarks.RemoveAll(bookmark => bookmark.Id == id) > 0;
        }
    }
}
