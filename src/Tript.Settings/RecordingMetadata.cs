// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

// The on-file metadata contract for a recording: what makes an
// existing recording readable — bookmark records, the audio track layout, the content type and
// the associated game. Compression is recorded in metadata rather than as a filename suffix;
// compression itself is out of scope, so the flag is load-only for alpha, nothing produces it.
using Tript.Core;

namespace Tript.Settings;

public sealed class RecordingMetadata
{
    // The link key back to the video, as the '/' separated path relative to the recording root
    // (for example "sessions/session-20260817-083000.mp4"). The metadata lives in a separate
    // metadata/ tree — never next to the video — so this field is how a record is connected to
    // the file it describes.
    public string VideoPath { get; set; } = string.Empty;

    // The game this recording belongs to, when a game was associated with it. Free-form by
    // choice: the game may be a known catalogue entry or an ad-hoc attribution made at recovery
    // time, and the metadata is not the place to enforce the catalogue.
    public string? Game { get; set; }

    public ContentType ContentType { get; set; } = ContentType.Recording;

    public DateTime StartTime { get; set; }

    // The user-facing title, when the user renamed the recording (RenameContent). Null or empty
    // means the file-name-without-extension is the title.
    public string? Title { get; set; }

    public bool Favorite { get; set; }

    // The recording's playing length in seconds, as the container reports it. Null on a record
    // written before the length was known; the library then shows no length for that item until it
    // is filled in.
    public double? DurationSeconds { get; set; }

    // The audio track layout: which track holds which device. The layout is part of the
    // recording's metadata contract and is preserved across compression.
    public List<AudioTrackLayout> AudioTracks { get; set; } = [];

    public List<Bookmark> Bookmarks { get; set; } = [];

    // Load-only for alpha: compression is out of scope, so nothing produces this flag, but files
    // that carry it must still load.
    public bool Compressed { get; set; }
}

// A single track's place in the output file. Sources is not a list of devices: a track is a
// destination that carries one or more sources, each with its own volume.
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
