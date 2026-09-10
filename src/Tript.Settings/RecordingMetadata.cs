// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Core;

namespace Tript.Settings;

public sealed class RecordingMetadata
{
    public string VideoPath { get; set; } = string.Empty;

    public string? Game { get; set; }

    public string? GameId { get; set; }

    public ContentType ContentType { get; set; } = ContentType.Recording;

    public DateTime StartTime { get; set; }

    public string? Title { get; set; }

    public bool Favorite { get; set; }

    public double? DurationSeconds { get; set; }

    public List<AudioTrackLayout> AudioTracks { get; set; } = [];

    public List<Bookmark> Bookmarks { get; set; } = [];

    public bool Compressed { get; set; }
}

public sealed class AudioTrackLayout
{
    public int Index { get; set; }

    public string Name { get; set; } = string.Empty;

    public List<SourceOnTrack> Sources { get; set; } = [];
}

public sealed class SourceOnTrack
{
    public string Name { get; set; } = string.Empty;

    public float Volume { get; set; } = 1.0f;
}
