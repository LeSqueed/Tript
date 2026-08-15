// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

// The on-file metadata contract for a recording (spec/config-and-storage.md): what makes an
// existing recording readable — bookmark records, the audio track layout, the content type and
// the associated game. Compression is recorded in metadata rather than as a filename suffix;
// compression itself is out of scope, so the flag is load-only for alpha, nothing produces it.
using Tript.Core;

namespace Tript.Settings;

public sealed class RecordingMetadata
{
    // The game this recording belongs to, when a game was associated with it. Free-form by
    // choice: the game may be a known catalogue entry or an ad-hoc attribution made at recovery
    // time, and the metadata is not the place to enforce the catalogue.
    public string? Game { get; set; }

    public ContentType ContentType { get; set; } = ContentType.Recording;

    public DateTime StartTime { get; set; }

    // The audio track layout: which track holds which device. The layout is part of the
    // recording's metadata contract and is preserved across compression.
    public List<AudioTrackLayout> AudioTracks { get; set; } = [];

    public List<Bookmark> Bookmarks { get; set; } = [];

    // Load-only for alpha: compression is out of scope, so nothing produces this flag, but files
    // that carry it must still load.
    public bool Compressed { get; set; }
}

// A single track's place in the output file. Sources is not a list of devices: a track is a
// destination that carries one or more sources, each with its own volume — the multi-track model
// from spec/recorder.md.
public sealed class AudioTrackLayout
{
    public int Index { get; set; }

    public string Name { get; set; } = string.Empty;

    public List<SourceOnTrack> Sources { get; set; } = [];
}

// One source routed into a track: which device, and the volume it was recorded at. Volume is
// per-source, not per-track.
public sealed class SourceOnTrack
{
    public string Name { get; set; } = string.Empty;

    public float Volume { get; set; } = 1.0f;
}
