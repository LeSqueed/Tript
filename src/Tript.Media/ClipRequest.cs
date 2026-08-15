// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Media;

// Everything the engine needs to turn part of a recorded session into one or more clip files. This
// is the engine-facing surface of the CreateClip payload: the IPC handler maps the wire message
// onto this and hands it over. Fields are named for what they are, not for their wire casing, which
// the contract itself admits is inconsistent.
public sealed class ClipRequest
{
    // The finished recording being clipped.
    public required string SourcePath { get; init; }

    // The marked regions, in timeline order. In Separate mode each becomes its own file; in Combine
    // mode they are concatenated into one.
    public required IReadOnlyList<ClipRegion> Regions { get; init; }

    public required ClipMode Mode { get; init; }

    // Where the clip(s) are written. In Separate mode this is a directory and one file per region
    // is created inside it; in Combine mode this is the single output file path.
    public required string OutputPath { get; init; }

    // Optional per-track adjustments applied via ffmpeg's volume filter. Indexed by the source
    // file's audio track order (0-based among audio streams).
    public IReadOnlyList<AudioTrackAdjustment> AudioTrackAdjustments { get; init; } = [];

    // The encoder family to target. Defaults to the generic software path, libx265, which can carry
    // 10-bit and therefore preserves a uniform HDR source. Selecting a non-10-bit family (for
    // example "libx264") forces the HDR source through the tone-map fallback, exactly as the spec's
    // decision rule describes: "can the target codec carry 10-bit".
    public string EncoderFamily { get; init; } = "libx265";

    // A low-latency "one line of ffmpeg stderr" channel, in the same spirit as the training
    // surface's progress messages. Invoked on the engine's worker thread as ffmpeg streams output.
    public Action<ClipProgress>? Progress { get; init; }
}
