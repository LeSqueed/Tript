// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;

namespace Tript.Recorder;

// The recorder's view of the thing that writes a recording to disk. The concrete implementation
// wraps an ObsOutput and the encoders that feed it; the state machine sees only this surface, which
// is what keeps the recorder testable without libobs and what keeps the two-output shape (Buffer,
// Hybrid) from disturbing the machine — a second output is just a second IRecorderOutput.
public interface IRecorderOutput : IDisposable
{
    // True while the output is actively writing. Read from the control plane.
    bool IsActive { get; }

    // Begins writing. False means the output refused to start; LastError carries the plugin's
    // reason when it set one. Success means the output has begun, not that bytes are on disk — the
    // Stopped event is what says how it ended.
    bool Start();

    // Asks the output to stop. The completion is the Stopped event, not this call's return.
    void Stop();

    // Waits for native stop completion and callback quiescence before the output is released.
    bool WaitForStop(TimeSpan timeout);

    // The plugin's failure report, borrowed and possibly null. Only meaningful when Start returned
    // false; the Stopped event carries the failure text for a stop.
    string? LastError { get; }

    // The output's stop signal. Fires exactly once per Start, whether the end was clean
    // (ObsOutputStopCode.Success) or a failure.
    event EventHandler<ObsOutputStopEvent>? Stopped;
}
