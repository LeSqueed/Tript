// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;

namespace Tript.Recorder;

// The recorder receives an already-resolved mode (a RecordingMode from the settings schema, via
// ResolvedRecorderSettings) and decides which explicitly selected outputs to run.
public static class RecordingModeExtensions
{
    public static bool RecordsSession(this RecordingMode mode) => mode is not RecordingMode.ReplayBufferOnly;

    public static bool UsesReplayBuffer(this RecordingMode mode) =>
        mode is RecordingMode.SessionWithReplayBuffer or RecordingMode.ReplayBufferOnly;

    public static bool IsAlphaSupported(this RecordingMode mode) =>
        mode is RecordingMode.Session or RecordingMode.SessionWithReplayBuffer or RecordingMode.ReplayBufferOnly;
}
