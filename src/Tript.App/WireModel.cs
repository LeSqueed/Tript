// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Detection;

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

// One audio track of a piece of content, as the library reports it.
internal sealed class AudioTrackInfo
{
    // Position in the file's audio stream order. This — not the settings Guid — is what the clip
    // engine keys adjustments by, and what the content server would select on.
    public int Index { get; set; }

    // The name from settings, e.g. "Game" or "Discord". Empty when the layout named nothing.
    public string Name { get; set; } = string.Empty;
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

    public string? GameId { get; set; }

    // When the content starts, as unix seconds. The metadata record's StartTime when there is one —
    // the authoritative capture time — and the file's last-write time otherwise.
    public double? StartTime { get; set; }

    // Unchanged: the end offset a metadata record declares, in seconds into the media. Nothing
    // populates it today, and the player already prefers the media's own measured duration over it.
    // The library's duration is DurationSeconds, not this.
    public double? EndTime { get; set; }

    // The audio tracks the file carries, in stream order, with the names the user gave them in
    // settings. Populated from the recording's metadata record; a clip inherits its source
    // session's layout, since a clip keeps every track of the session it was cut from. Null when
    // nothing knows — an imported file, or a session recorded before a layout was written.
    public List<AudioTrackInfo>? AudioTracks { get; set; }

    // The content's playing length in seconds, or null when it is not known yet. Persisted (the
    // recording's metadata record, the clip's own record) rather than measured per list, so the
    // library can show a length without loading the video and without an ffprobe per item per push.
    public double? DurationSeconds { get; set; }

    // The file's size on disk. The library sorts and reports on it, and it is free to read while the
    // directory is being enumerated.
    public long FileSizeBytes { get; set; }

    // Present only for a synthetic recording whose source video is no longer on disk.
    public bool? VideoMissing { get; set; }

    public bool? HighlightsOnly { get; set; }

    // Present only for the session the active recording is writing right now. The library renders it
    // as a live capture: recording in progress, non-interactive until it has highlights.
    public bool? Recording { get; set; }

    // The bookmarks the recording carries on the wire, null when the item has none (clips never
    // have bookmarks). The wire shape mirrors the frontend's BookmarkItem (protocol.ts).
    public List<BookmarkItem>? Bookmarks { get; set; }

    // True when the recording has at least one detected event the automatic-highlights pipeline
    // would cut, so the frontend can keep the "Create highlights" action disabled when there is
    // nothing to create. Absent (null) on clips, where the action never applies.
    public bool? HasAutomaticClipCandidates { get; set; }

    // Set only on generated highlights. Manual clips remain independent library content.
    public bool Automated { get; set; }

    public string? SourceSessionPath { get; set; }

    public double? ClipStartTime { get; set; }

    public double? ClipEndTime { get; set; }

    public bool AutomaticClipsProcessing { get; set; }

    public bool AutomaticClipsPaused { get; set; }

    public int? AutomaticClipsCompleted { get; set; }

    public int? AutomaticClipsTotal { get; set; }

    public bool? IsHdr { get; set; }
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

    // The exact executable path a custom game matches, or the launcher-confirmed path of a packaged
    // game. Null when the game is matched by basename (the packaged overlay entries).
    public string? ExecutablePath { get; set; }

    // True when the identity comes from the packaged catalogue, false for a user-defined custom
    // game. The frontend uses this to keep packaged identities immutable.
    public bool BuiltIn { get; set; }

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

internal sealed class CreateAutomaticClipsParameters
{
    public string FilePath { get; set; } = string.Empty;
}

internal sealed class StartRecordingParameters
{
    public string? GameId { get; set; }
    public bool? ApplyDisplay { get; set; }
    public string? DisplayId { get; set; }
}

internal sealed class OpenFileLocationParameters
{
    public string FilePath { get; set; } = string.Empty;
}

internal sealed class ConvertToSdrParameters
{
    public string Id { get; set; } = string.Empty;
    public string ContentType { get; set; } = "clip";
    public string FilePath { get; set; } = string.Empty;
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

    // Applies the same operation to eligible automatic highlights linked to a recording.
    public bool DeleteLinkedHighlights { get; set; }
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

    public string RequestId { get; set; } = string.Empty;
}

internal sealed class SelectGameExecutableParameters
{
    public string RequestId { get; set; } = string.Empty;
}

internal sealed class SearchGamesParameters
{
    public string RequestId { get; set; } = string.Empty;

    public string Query { get; set; } = string.Empty;

    public int Limit { get; set; } = 20;
}

internal sealed class ResolveGameSearchParameters
{
    public string RequestId { get; set; } = string.Empty;

    public string Input { get; set; } = string.Empty;
}

internal sealed class GameCandidateParameters
{
    public string RequestId { get; set; } = string.Empty;

    public string ExecutablePath { get; set; } = string.Empty;
}

internal sealed class AddGameCandidateParameters
{
    public string RequestId { get; set; } = string.Empty;

    public string? Name { get; set; }

    public string ExecutablePath { get; set; } = string.Empty;
}

internal sealed class GameRecordingConfirmParameters
{
    public string PromptId { get; set; } = string.Empty;

    public bool Record { get; set; }
}

internal sealed class PublishTrainingModelParameters
{
    public string RequestId { get; set; } = string.Empty;
    public string GameId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
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

#if TRIPT_TRAINING

internal sealed class TrainingGameParameters
{
    public string GameId { get; set; } = string.Empty;
}

internal sealed class AvailableRecordingModelsMessage
{
    public List<AvailableRecordingModel> Models { get; set; } = [];
}

internal sealed class AvailableRecordingModel
{
    public string GameId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

internal sealed class ImportTrainingParameters
{
    public string GameId { get; set; } = string.Empty;

    public string SourcePath { get; set; } = string.Empty;

    public bool ConfirmOverwrite { get; set; }

    public string? ExpectedRevision { get; set; }
}

internal sealed class TrainingLabelParameters
{
    public int ClassId { get; set; }

    public double CenterX { get; set; }

    public double CenterY { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }
}

internal sealed class TrainingOcrTranscriptionParameters
{
    public int EventId { get; set; }

    public string SegmentId { get; set; } = "default";

    public string LanguageTag { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;
}

internal sealed class CaptureTrainingSampleParameters
{
    public string GameId { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public double TimestampSeconds { get; set; }

    public int ImageWidth { get; set; }

    public int ImageHeight { get; set; }

    public List<TrainingLabelParameters> Labels { get; set; } = [];
}

internal sealed class UpdateTrainingSampleParameters
{
    public string GameId { get; set; } = string.Empty;

    public string SampleId { get; set; } = string.Empty;

    public string RequestId { get; set; } = string.Empty;

    public List<TrainingLabelParameters> Labels { get; set; } = [];

    public List<TrainingOcrTranscriptionParameters> OcrTranscriptions { get; set; } = [];
}

internal sealed class UpdateTrainingEventsParameters
{
    public string GameId { get; set; } = string.Empty;

    public string RequestId { get; set; } = string.Empty;

    public List<EventDefinition> Events { get; set; } = [];
}

internal sealed class UpdateTrainingRegionGroupsParameters
{
    public string GameId { get; set; } = string.Empty;

    public string RequestId { get; set; } = string.Empty;

    public List<Training.TrainingRegionGroup> RegionGroups { get; set; } = [];
}

internal sealed class TrainingSampleParameters
{
    public string GameId { get; set; } = string.Empty;

    public string SampleId { get; set; } = string.Empty;

    public bool PreviewOnly { get; set; }

    public string? RequestId { get; set; }
}

internal sealed class SuggestTrainingLabelsParameters
{
    public string GameId { get; set; } = string.Empty;

    public string SampleId { get; set; } = string.Empty;

    public string? RequestId { get; set; }
}

internal sealed class StartTrainingParameters
{
    public string GameId { get; set; } = string.Empty;

    public int? ImageSize { get; set; }

    public int Epochs { get; set; } = 100;

    public string Device { get; set; } = "auto";

    public string? BaseModel { get; set; }

    // Extra mildly-distorted copies of every training crop. Validation is never augmented.
    public int? AugmentCopies { get; set; }
}

#endif
