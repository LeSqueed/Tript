// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Detection;

namespace Tript.App;

internal static class Wire
{
    internal static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

internal sealed class StreamerStatusInfo
{
    public string State { get; set; } = "off";

    public bool ShareEnabled { get; set; }

    public bool ObsRunning { get; set; }

    public string? ObsVersion { get; set; }

    public string SenderName { get; set; } = string.Empty;

    public uint Width { get; set; }

    public uint Height { get; set; }

    public string? AdapterName { get; set; }

    public bool HookConflictSuspected { get; set; }
}

internal sealed class AudioTrackInfo
{
    public int Index { get; set; }

    public string Name { get; set; } = string.Empty;
}

internal sealed class ContentItem
{
    public string ContentType { get; set; } = "recording";

    public string FileName { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public string? Title { get; set; }

    public bool Favorite { get; set; }

    public string? Game { get; set; }

    public string? GameId { get; set; }

    public double? StartTime { get; set; }

    public double? EndTime { get; set; }

    public List<AudioTrackInfo>? AudioTracks { get; set; }

    public double? DurationSeconds { get; set; }

    public long FileSizeBytes { get; set; }

    public bool? VideoMissing { get; set; }

    public bool? HighlightsOnly { get; set; }

    public bool? Recording { get; set; }

    public List<BookmarkItem>? Bookmarks { get; set; }

    public bool? HasAutomaticClipCandidates { get; set; }

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

    public string Name { get; set; } = string.Empty;

    public string? Executable { get; set; }

    public string? ExecutablePath { get; set; }

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

internal sealed class OpenInBrowserParameters
{
    public string Url { get; set; } = string.Empty;
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

    public string FileName { get; set; } = string.Empty;

    public bool Permanent { get; set; }

    public bool DeleteLinkedHighlights { get; set; }
}

internal sealed class DeleteMultipleContentParameters
{
    public List<DeleteContentParameters> Items { get; set; } = [];

    public bool Permanent { get; set; }
}

internal sealed class RestoreTrashParameters
{
    public List<string> EntryIds { get; set; } = [];
}

internal sealed class PurgeTrashParameters
{
    public List<string>? EntryIds { get; set; }
}

internal sealed class TrashEntry
{
    public string Id { get; set; } = string.Empty;

    public string ContentType { get; set; } = "recording";

    public string FileName { get; set; } = string.Empty;

    public string? Title { get; set; }

    public string? Game { get; set; }

    public double? DurationSeconds { get; set; }

    public long? FileSizeBytes { get; set; }

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

internal sealed class RequestGameAddParameters
{
    public string RequestId { get; set; } = string.Empty;

    public string GameId { get; set; } = string.Empty;
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

internal sealed class PublishTrainingModelParameters
{
    public string RequestId { get; set; } = string.Empty;
    public string GameId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
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

internal sealed class TrainingOcrRegionParameters
{
    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

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

    public List<TrainingOcrRegionParameters> OcrRegions { get; set; } = [];
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

    public int? AugmentCopies { get; set; }

    public string? Scope { get; set; }

    public int? OcrEpochs { get; set; }
}

#endif
