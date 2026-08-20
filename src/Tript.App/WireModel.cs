// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.App;

// The IPC wire model. These are the shapes the
// frontend narrows on, so the field names and casing are a compatibility surface — not a free
// choice. The serialization options are shared so every push behaves identically.
internal static class Wire
{
    internal static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

internal sealed class ContentItem
{
    public string ContentType { get; set; } = "recording";

    public string FileName { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public string? Title { get; set; }

    public bool Favorite { get; set; }

    // The game this content belongs to, or null when nothing associated one with it. The library
    // filters and groups by this, so it is populated for clips too — inherited from the source
    // session, since a clip has no metadata record of its own (see AppHost.InheritedGame).
    public string? Game { get; set; }

    // When the content starts, as unix seconds. The metadata record's StartTime when there is one —
    // the authoritative capture time — and the file's last-write time otherwise.
    public double? StartTime { get; set; }

    // Unchanged: the end offset a metadata record declares, in seconds into the media. Nothing
    // populates it today, and the player already prefers the media's own measured duration over it.
    // The library's duration is DurationSeconds, not this.
    public double? EndTime { get; set; }

    // The content's playing length in seconds, or null when it is not known yet. Persisted (the
    // recording's metadata record, the clip's own record) rather than measured per list, so the
    // library can show a length without loading the video and without an ffprobe per item per push.
    public double? DurationSeconds { get; set; }

    // The file's size on disk. The library sorts and reports on it, and it is free to read while the
    // directory is being enumerated.
    public long FileSizeBytes { get; set; }

    // The bookmarks the recording carries on the wire, null when the item has none (clips never
    // have bookmarks). The wire shape mirrors the frontend's BookmarkItem (protocol.ts).
    public List<BookmarkItem>? Bookmarks { get; set; }
}

internal sealed class GameInfo
{
    public string Id { get; set; } = string.Empty;

    // The display name, and only that. What the game runs as is Executable.
    public string Name { get; set; } = string.Empty;

    // The process/executable name auto-detection matches and game capture hooks, already resolved
    // from the settings entry (GameSetting.EffectiveExecutable), so it is never the empty string.
    // Null only for a catalogue entry that carries no executable at all.
    public string? Executable { get; set; }

    public bool Detected { get; set; }
}

internal sealed class BookmarkItem
{
    public string Id { get; set; } = string.Empty;

    public string Type { get; set; } = "Manual";

    public string? Subtype { get; set; }

    public double Time { get; set; }

    public string? Label { get; set; }
}

// The CreateClip command's parameters (protocol.ts CreateClipParameters).
internal sealed class CreateClipParameters
{
    public string Id { get; set; } = string.Empty;

    public string Type { get; set; } = "clip";

    public string? Game { get; set; }

    public int? IgdbId { get; set; }

    public string FileName { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public double StartTime { get; set; }

    public double EndTime { get; set; }

    public List<ClipSegment> Segments { get; set; } = [];

    public string OutputMode { get; set; } = "combine";

    public Dictionary<string, double>? AudioTrackVolumes { get; set; }

    public List<string>? MutedAudioTracks { get; set; }
}

internal sealed class ClipSegment
{
    public double StartTime { get; set; }

    public double EndTime { get; set; }
}

internal sealed class DeleteContentParameters
{
    public string ContentType { get; set; } = "recording";

    // The video's path RELATIVE to the content root (e.g. "sessions/session-1.mp4"), never the bare
    // file name: it is resolved against the root, and a bare name would not find the file.
    public string FileName { get; set; } = string.Empty;

    // Omitted or false moves the item to the trash; true unlinks it there and then.
    public bool Permanent { get; set; }
}

internal sealed class DeleteMultipleContentParameters
{
    public List<DeleteContentParameters> Items { get; set; } = [];

    // Applies to the whole batch, and wins over a per-item flag.
    public bool Permanent { get; set; }
}

internal sealed class RestoreTrashParameters
{
    public List<string> EntryIds { get; set; } = [];
}

internal sealed class PurgeTrashParameters
{
    // Absent means the whole bin; an empty list means nothing, so a client can never empty the
    // trash by accident.
    public List<string>? EntryIds { get; set; }
}

// One item in the trash, as the `trash` push spells it.
internal sealed class TrashEntry
{
    public string Id { get; set; } = string.Empty;

    public string ContentType { get; set; } = "recording";

    public string FileName { get; set; } = string.Empty;

    public string? Title { get; set; }

    public string? Game { get; set; }

    public double? DurationSeconds { get; set; }

    public long? FileSizeBytes { get; set; }

    // Epoch seconds, both. PurgeAt is 0 when the retention is disabled.
    public long DeletedAt { get; set; }

    public long PurgeAt { get; set; }
}

internal sealed class RenameContentParameters
{
    public string ContentType { get; set; } = "recording";

    public string FileName { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
}

internal sealed class ToggleFavoriteParameters
{
    public string ContentType { get; set; } = "recording";

    public string FilePath { get; set; } = string.Empty;

    public bool Favorite { get; set; }
}

internal sealed class AddBookmarkParameters
{
    public string ContentType { get; set; } = "recording";

    public string FilePath { get; set; } = string.Empty;

    public string Id { get; set; } = string.Empty;

    public double Time { get; set; }

    public string Type { get; set; } = "Manual";
}

internal sealed class DeleteBookmarkParameters
{
    public string ContentType { get; set; } = "recording";

    public string FilePath { get; set; } = string.Empty;

    public string Id { get; set; } = string.Empty;
}

internal sealed class UpdateSettingsParameters
{
    public System.Text.Json.JsonElement Settings { get; set; }
}

internal sealed class ToggleFullscreenParameters
{
    public bool Enabled { get; set; }
}

internal sealed class RecoveryConfirmParameters
{
    public string RecoveryId { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public string? GameOverride { get; set; }
}

internal sealed class NewConnectionParameters
{
    public int ProtocolVersion { get; set; }
}

internal sealed class CancelClipParameters
{
    public string Id { get; set; } = string.Empty;
}
