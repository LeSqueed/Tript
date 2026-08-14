// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Core;

// The recording in progress, as everything that annotates one needs to see it: when it started,
// so an event's wall-clock time can be turned into an offset into the file, and somewhere to put
// the result. Deliberately not the whole recording — the producers of bookmarks have no business
// with its file name, its game or its encoder settings.
public interface IRecordingSession
{
    DateTime StartTime { get; }

    // Called from whichever thread noticed the event, including a detector's own inference thread.
    // Implementations own the synchronisation.
    void AddBookmark(Bookmark bookmark);
}

public static class RecordingSessionRegistry
{
    private static Func<IRecordingSession?>? _resolver;

    // Null whenever nothing is being recorded, which is the normal state and not an error: an
    // event detected outside a recording has nowhere to be written and is dropped.
    public static IRecordingSession? Active => Volatile.Read(ref _resolver)?.Invoke();

    // A resolver rather than a stored session: recordings start and stop while the detectors that
    // write into them stay up, so the answer has to be recomputed per call.
    public static void SetResolver(Func<IRecordingSession?> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        Volatile.Write(ref _resolver, resolver);
    }

    public static void Reset() => Volatile.Write(ref _resolver, null);
}
