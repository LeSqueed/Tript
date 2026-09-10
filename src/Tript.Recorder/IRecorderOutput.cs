// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;

namespace Tript.Recorder;

public interface IRecorderOutput : IDisposable
{
    bool IsActive { get; }

    bool Start();

    void Stop();

    bool WaitForStop(TimeSpan timeout);

    string? LastError { get; }

    event EventHandler<ObsOutputStopEvent>? Stopped;
}

public interface IReplayBufferOutput
{
    bool SaveReplay(string directory, string format, Action<string> onSaved);

    bool WaitForReplaySave(TimeSpan timeout);
}
