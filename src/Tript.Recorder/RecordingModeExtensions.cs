// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;

namespace Tript.Recorder;

// The recorder receives an already-resolved mode (a RecordingMode from the settings schema, via
// ResolvedRecorderSettings) and must decide what it actually runs. For alpha that decision is
// deliberately narrow — Session only — but it is drawn as a first-class seam so the Buffer and
// Hybrid outputs slot into it without a refactor.
public static class RecordingModeExtensions
{
    // Whether this mode asks for a session recording. Session and Hybrid both write a continuous
    // file while running; a Buffer-only mode writes nothing.
    public static bool RecordsSession(this RecordingMode mode) => mode is RecordingMode.Session or RecordingMode.Hybrid;

    // Whether this mode is one the alpha recorder can actually run. Buffer and Hybrid are designed
    // for but deferred, so a resolved config that lands on either is a failure at start time — the
    // recorder must refuse loudly rather than silently record something else.
    public static bool IsAlphaSupported(this RecordingMode mode) => mode == RecordingMode.Session;
}
