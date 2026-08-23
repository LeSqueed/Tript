// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Core;
using Xunit;

namespace Tript.Recorder.Tests;

// The recorder tests that install an active recording (process-wide state via
// RecordingSessionRegistry) run one at a time. The resolver is shared with the detection suite's
// RecordingStateCollection, but xunit runs assemblies in separate processes, so each suite needs
// its own collection — a cross-assembly collection would couple the two test projects.
[CollectionDefinition(Name)]
public sealed class RecorderRecordingCollection
{
    public const string Name = "recorder active recording";
}

// Installs an active recording for the duration of a test body and restores the registry
// afterwards. Bookmark-producing components (the detection host) write
// through the active session, so this is how a test observes what they produced.
public sealed class ActiveRecordingScope : IDisposable
{
    private readonly IRecordingSession _recording;

    public ActiveRecordingScope(IRecordingSession recording)
    {
        _recording = recording;
        RecordingSessionRegistry.SetResolver(() => _recording);
    }

    public void Dispose() => RecordingSessionRegistry.Reset();
}
