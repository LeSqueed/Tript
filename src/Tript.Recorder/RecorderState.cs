// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Recorder;

// The states of the recorder state machine. Idle is the resting state; Recording is an output that
// is active and being written; Stopping is the window between the stop request and the output's
// stop signal. The effective mode (Session only for alpha) is a property of the running recording,
// not a state of its own — a start in an unsupported mode leaves the recorder in Idle and reports
// why.
public enum RecorderState
{
    Idle,

    Recording,

    // Between the call to Stop and the output's stop signal. The recorder stays here until the
    // output confirms, so a recording that never emits a stop signal is visible as a stuck stop.
    Stopping
}
