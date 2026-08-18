// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Media;

// Everything the engine needs to turn part of a recorded session into one or more clip files. This
// is the engine-facing surface of the CreateClip payload: the IPC handler maps the wire message
// onto this and hands it over.
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
    // 10-bit and therefore preserves a uniform HDR source.
    public string EncoderFamily { get; init; } = "libx265";

    // The user's clip title from the clip dialog ("The clutch"), carried through so the host can
    // persist it against the finished clip(s). Empty means the user set no title and the clip
    // falls back to its file-name-without-extension.
    public string Title { get; init; } = string.Empty;

    // A low-latency "one line of ffmpeg stderr" channel, in the same spirit as the training
    // surface's progress messages. Invoked on the engine's worker thread as ffmpeg streams output.
    public Action<ClipProgress>? Progress { get; init; }
}
