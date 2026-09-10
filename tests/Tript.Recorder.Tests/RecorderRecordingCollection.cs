// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Core;
using Xunit;

namespace Tript.Recorder.Tests;

[CollectionDefinition(Name)]
public sealed class RecorderRecordingCollection
{
    public const string Name = "recorder active recording";
}

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
