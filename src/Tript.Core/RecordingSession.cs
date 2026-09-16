// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Core;

public interface IRecordingSession
{
    DateTime StartTimeUtc { get; }

    // Called from detector threads; implementations own the synchronisation.
    void AddBookmark(Bookmark bookmark);

    bool RemoveBookmark(Guid id);
}

public static class RecordingSessionRegistry
{
    private static Func<IRecordingSession?>? _resolver;

    public static IRecordingSession? Active => Volatile.Read(ref _resolver)?.Invoke();

    public static void SetResolver(Func<IRecordingSession?> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        Volatile.Write(ref _resolver, resolver);
    }

    public static void Reset() => Volatile.Write(ref _resolver, null);
}
