// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Recorder;

// Why a recording is no longer running. UserRequested and GameStopped are the normal ends —
// someone asked, or the detected game went away. Everything else is a failure the recorder must
// surface, never swallow: the stop code from the binding is the reason a recording the recorder
// expected to keep going actually ended. Success is not a reason here because a stop it asked for
// is not a stop it needs explaining; the code's own reason maps to a distinct recorder reason.
public enum RecorderStopReason
{
    // The recorder was told to stop, and the output reported a clean end.
    UserRequested,

    // The game that auto-started the recording was no longer detected running.
    GameStopped,

    // The output refused to start: the synchronous obs_output_start false channel.
    StartRefused,

    // The output stopped on its own with a non-success stop code.
    OutputFailure,

    // The resolved settings say a mode the alpha recorder cannot run.
    UnsupportedMode,

    // The recorder is being disposed mid-recording.
    Disposed
}
