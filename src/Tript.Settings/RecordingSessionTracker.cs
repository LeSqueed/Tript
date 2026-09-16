// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Core;

namespace Tript.Settings;

public sealed class RecordingSessionTracker
{
    private readonly object _gate = new();

    private RecordingSession? _current;

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

    public RecordingSession Start(DateTime? startTimeUtc = null)
    {
        var session = new RecordingSession(startTimeUtc ?? DateTime.UtcNow);
        lock (_gate)
        {
            _current = session;
        }

        return session;
    }

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
