// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Recorder;

public enum RecorderStopReason
{
    UserRequested,

    GameStopped,

    StartRefused,

    OutputFailure,

    UnsupportedMode,

    Disposed
}
