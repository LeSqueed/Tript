// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Core;

namespace Tript.Settings;

public sealed class RecordingSession : IRecordingSession
{
    private readonly object _gate = new();

    private readonly List<Bookmark> _bookmarks = [];

    public DateTime StartTimeUtc { get; }

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

    public RecordingSession(DateTime startTimeUtc)
    {
        StartTimeUtc = startTimeUtc.Kind == DateTimeKind.Local ? startTimeUtc.ToUniversalTime() : startTimeUtc;
    }

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
