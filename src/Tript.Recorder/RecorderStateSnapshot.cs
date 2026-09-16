// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;

namespace Tript.Recorder;

public sealed record RecorderStateSnapshot(
    RecorderState State,
    RecorderStopReason? LastStopReason,
    ObsOutputStopCode? LastStopCode,
    string? LastError)
{
    public static RecorderStateSnapshot Idle => new(RecorderState.Idle, null, null, null);
}
