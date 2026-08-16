// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;

namespace Tript.Recorder;

// What a consumer of the recorder — the IPC `state` message, the UI — sees at a point in time:
// the state, why the last recording stopped (null while one is in flight), and the statistics the
// output reported when it ended. Immutable, so it can be handed to a thread that is not the
// recorder's without the recorder mutating underneath it.
public sealed record RecorderStateSnapshot(
    RecorderState State,
    RecorderStopReason? LastStopReason,
    ObsOutputStopCode? LastStopCode,
    string? LastError)
{
    public static RecorderStateSnapshot Idle => new(RecorderState.Idle, null, null, null);
}
