// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

// Owns the currently-active recording session and registers the resolver with the core registry,
// so the detector (which writes bookmarks through RecordingSessionRegistry.Active) and the
// recorder (which creates and stops sessions) agree on what is recording without sharing a
// reference. Resolution is re-evaluated per call: the registry invokes this tracker's resolver
// every time, and the resolver reads the current session, so a recording that starts or stops
// while a detector stays up is seen on the next lookup.
using Tript.Core;

namespace Tript.Settings;

public sealed class RecordingSessionTracker
{
    private readonly object _gate = new();

    private RecordingSession? _current;

    // Registers this tracker as the process-wide resolver for the active recording. Call once at
    // startup; the tracker then stays registered for the process, returning whatever session is
    // current (or none, which is the normal state and not an error).
    public RecordingSessionTracker Register()
    {
        RecordingSessionRegistry.SetResolver(() =>
        {
            lock (_gate)
            {
                return _current;
            }
        });

        return this;
    }

    public RecordingSession? Active
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    // Begins a new recording session and makes it the active one. Any prior session is simply
    // superseded — the caller is expected to have stopped it.
    public RecordingSession Start(DateTime? startTimeUtc = null)
    {
        var session = new RecordingSession(startTimeUtc ?? DateTime.UtcNow);
        lock (_gate)
        {
            _current = session;
        }

        return session;
    }

    // Ends the active session and returns it (for metadata serialization), or null when nothing
    // was recording.
    public RecordingSession? Stop()
    {
        lock (_gate)
        {
            var session = _current;
            _current = null;
            return session;
        }
    }
}
