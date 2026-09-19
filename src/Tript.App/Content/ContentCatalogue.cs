// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.IO.Enumeration;
using Tript.Core;
using Tript.Settings;

namespace Tript.App.Content;

internal sealed record LibraryState(
    string? ActiveRecordingPath,
    RecordingMetadata? PendingMetadata,
    IReadOnlyList<Bookmark> LiveBookmarks,
    AutomaticClipProgress? AutomaticClips);

internal sealed record AutomaticClipProgress(string SourceSessionPath, bool Paused, int Completed, int Total);

internal abstract record LibraryBackfill
{
    private LibraryBackfill()
    {
    }

    internal sealed record RecordingDuration(string FileName, string WirePath, double Seconds) : LibraryBackfill;

    internal sealed record ClipDuration(string FileName, double Seconds) : LibraryBackfill;

    internal sealed record ClipHdr(string FileName, bool IsHdr) : LibraryBackfill;

    internal sealed record ClipGame(string FileName, string? Game, string? GameId) : LibraryBackfill;
}

internal sealed class ContentCatalogue
{
    private static readonly EnumerationOptions LibraryEnumeration = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
    };

    private static readonly string VideoPattern = FileSystemName.TranslateWin32Expression("*.mp4");

    private static StringComparer PathComparer => FilePaths.Comparer;

    private static StringComparison PathComparison => FilePaths.Comparison;

    private readonly string _root;
    private readonly RecordingMetadataStore _metadata;
    private readonly ClipTitleStore _clipTitles;
    private readonly LibraryProbe _probe;
    private readonly LibraryGames _games;
    private readonly Action<LibraryBackfill> _backfill;

    internal ContentCatalogue(string root, RecordingMetadataStore metadata, ClipTitleStore clipTitles,
        LibraryProbe probe, LibraryGames games, Action<LibraryBackfill> backfill)
    {
        _root = root;
        _metadata = metadata;
        _clipTitles = clipTitles;
        _probe = probe;
        _games = games;
        _backfill = backfill;
    }

    internal List<ContentItem> Build(LibraryState state)
    {
        var items = new List<ContentItem>();
        var root = new DirectoryInfo(_root);
        if (!root.Exists)
            return items;

        var clipRecords = _clipTitles.EnumerateRecords()
            .ToDictionary(entry => entry.ClipFileName, entry => entry.Record, PathComparer);
        var recordings = new RecordingLinks();
        var clips = new List<ContentItem>();
        var linkedAutomaticSources = new HashSet<string>(PathComparer);
        var highlightsOnlySources = new Dictionary<string, bool>(PathComparer);
        var recordingPaths = new HashSet<string>(PathComparer);
        var probeBudget = LibraryProbe.ProbesPerListing;

        foreach (var file in EnumerateVideos(root))
        {
            var relative = ContentLayout.ToWirePath(_root, file.FullName);
            var item = NewFileItem(file, relative, state.AutomaticClips);

            if (item.ContentType == "recording")
            {
                recordingPaths.Add(relative);
                DescribeRecording(item, file.Name, relative, state, recordings);
            }
            else
            {
                clipRecords.TryGetValue(file.Name, out var record);
                DescribeClip(item, record, linkedAutomaticSources, highlightsOnlySources);
                clips.Add(item);
            }

            item.StartTime ??= UnixSeconds(file.LastWriteTime);
            ProbeMissingFacts(item, file, relative, ref probeBudget);
            items.Add(item);
        }

        InheritSourceBookmarks(clips, clipRecords);

        var earliestLinkedHighlightStart = EarliestLinkedHighlightStart(clips);
        foreach (var sourcePath in linkedAutomaticSources)
        {
            if (recordingPaths.Contains(sourcePath))
                continue;

            items.Add(MissingSourceItem(sourcePath, state, recordings,
                highlightsOnlySources.TryGetValue(sourcePath, out var marked) && marked,
                earliestLinkedHighlightStart));
        }

        foreach (var clip in clips)
        {
            if (clip.Game is null && clip.GameId is null)
            {
                clip.Game = InheritedFrom(clip, recordings.GamesByPath, recordings.GamesBySession);
                clip.GameId = InheritedFrom(clip, recordings.GameIdsByPath, recordings.GameIdsBySession);
            }
            clipRecords.TryGetValue(clip.FileName, out var record);
            BackfillClipGame(clip, record);
            clip.AudioTracks = InheritedFrom(clip, recordings.TracksByPath, recordings.TracksBySession);
        }

        foreach (var item in items)
            item.Game = _games.DisplayName(item.Game, item.GameId);

        items.Sort((left, right) =>
        {
            var byDate = (right.StartTime ?? 0).CompareTo(left.StartTime ?? 0);
            return byDate != 0 ? byDate : string.CompareOrdinal(left.FilePath, right.FilePath);
        });

        return items;
    }

    internal static string? NormalizeSourcePath(string root, string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return null;

        var absolutePath = ContentServer.ResolveWithinRoot(root, sourcePath.Trim().Replace('\\', '/'));
        return absolutePath is null ? null : ContentLayout.ToWirePath(root, absolutePath);
    }

    internal static long SafeLength(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    internal static double? UnixSeconds(DateTime dateTime) =>
        dateTime == default ? null : new DateTimeOffset(dateTime).ToUnixTimeSeconds();

    private static IEnumerable<FileInfo> EnumerateVideos(DirectoryInfo root)
    {
        var rootPath = Path.TrimEndingDirectorySeparator(root.FullName);
        var ignoreCase = FilePaths.Comparison == StringComparison.OrdinalIgnoreCase;
        return new FileSystemEnumerable<FileInfo>(rootPath,
            (ref FileSystemEntry entry) => (FileInfo)entry.ToFileSystemInfo(), LibraryEnumeration)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                !entry.IsDirectory && FileSystemName.MatchesWin32Expression(VideoPattern, entry.FileName, ignoreCase),
            ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                !(ContentLayout.IsReservedSegment(new string(entry.FileName))
                    && Path.TrimEndingDirectorySeparator(entry.Directory).SequenceEqual(rootPath)),
        };
    }

    private static ContentItem NewFileItem(FileInfo file, string relative, AutomaticClipProgress? automaticClips)
    {
        var processing = automaticClips is not null
            && string.Equals(relative, automaticClips.SourceSessionPath, PathComparison);
        return new ContentItem
        {
            ContentType = ContentLayout.IsClipPath(relative) ? "clip" : "recording",
            FileName = file.Name,
            FilePath = relative,
            Title = Path.GetFileNameWithoutExtension(file.Name),
            FileSizeBytes = SafeLength(file),
            AutomaticClipsProcessing = processing,
            AutomaticClipsPaused = processing && automaticClips!.Paused,
            AutomaticClipsCompleted = processing ? automaticClips!.Completed : null,
            AutomaticClipsTotal = processing ? automaticClips!.Total : null,
        };
    }

    private void DescribeRecording(ContentItem item, string fileName, string relative, LibraryState state,
        RecordingLinks recordings)
    {
        var metadata = _metadata.Load(fileName);
        if (metadata is not null)
        {
            ApplyRecordingMetadata(item, metadata);
            recordings.Remember(item, fileName, relative);
        }
        else
        {
            item.Bookmarks = [];
        }

        if (!IsActiveRecording(relative, state))
            return;

        item.Recording = true;
        ApplyPendingGame(item, state.PendingMetadata);
        item.Bookmarks = MapBookmarks(state.LiveBookmarks);
    }

    private void DescribeClip(ContentItem item, ClipTitleRecord? record,
        HashSet<string> linkedAutomaticSources, Dictionary<string, bool> highlightsOnlySources)
    {
        item.SourceSessionPath = NormalizeSourcePath(_root, record?.SourceSessionPath);
        item.IsHdr = record?.IsHdr;
        item.ClipStartTime = record?.ClipStartTime;
        item.ClipEndTime = record?.ClipEndTime;
        if (record?.IsAutomatic == true)
        {
            item.ContentType = "highlight";
            item.Automated = true;
            if (item.SourceSessionPath is { } source)
            {
                linkedAutomaticSources.Add(source);
                var sourceIsHighlightsOnly = record.SourceSessionHighlightsOnly == true;
                highlightsOnlySources[source] = highlightsOnlySources.TryGetValue(source, out var current)
                    ? current && sourceIsHighlightsOnly
                    : sourceIsHighlightsOnly;
            }
        }
        if (!string.IsNullOrWhiteSpace(record?.Title))
            item.Title = record.Title;
        item.Favorite = record?.Favorite ?? false;
        item.DurationSeconds = record?.DurationSeconds;
        if (record is not null
            && (!string.IsNullOrWhiteSpace(record.Game) || !string.IsNullOrWhiteSpace(record.GameId)))
        {
            item.Game = string.IsNullOrWhiteSpace(record.Game) ? null : record.Game;
            item.GameId = _games.ResolveStoredGameId(record.GameId, item.Game);
        }
    }

    private void ProbeMissingFacts(ContentItem item, FileInfo file, string relative, ref int probeBudget)
    {
        if (item.Recording == true)
            return;

        var isRecording = item.ContentType == "recording";
        if (item.DurationSeconds is null && probeBudget > 0 && _probe.CanProbe(file.FullName))
        {
            probeBudget--;
            item.DurationSeconds = _probe.ReadDuration(file.FullName, relative);
            if (item.DurationSeconds is { } seconds)
            {
                _backfill(isRecording
                    ? new LibraryBackfill.RecordingDuration(file.Name, relative, seconds)
                    : new LibraryBackfill.ClipDuration(file.Name, seconds));
            }
        }

        if (!isRecording && item.IsHdr is null && probeBudget > 0 && _probe.CanProbe(file.FullName))
        {
            probeBudget--;
            item.IsHdr = _probe.ReadHdr(file.FullName, relative);
            if (item.IsHdr is { } isHdr)
                _backfill(new LibraryBackfill.ClipHdr(file.Name, isHdr));
        }
    }

    private void InheritSourceBookmarks(List<ContentItem> clips, Dictionary<string, ClipTitleRecord> clipRecords)
    {
        var sourceMetadata = new Dictionary<string, RecordingMetadata?>(PathComparer);
        foreach (var clip in clips)
        {
            if (clip.SourceSessionPath is null || !clipRecords.TryGetValue(clip.FileName, out var record))
                continue;

            var spans = SourceSpansOf(record);
            if (spans.Count == 0)
                continue;

            var sourceFileName = ContentLayout.FileNameOf(clip.SourceSessionPath);
            if (!sourceMetadata.TryGetValue(sourceFileName, out var metadata))
            {
                metadata = _metadata.Load(sourceFileName);
                sourceMetadata[sourceFileName] = metadata;
            }
            if (metadata is not null)
                clip.Bookmarks = InheritedBookmarks(metadata.Bookmarks, spans, clip.DurationSeconds);
        }
    }

    private static Dictionary<string, double> EarliestLinkedHighlightStart(List<ContentItem> clips)
    {
        var earliest = new Dictionary<string, double>(PathComparer);
        foreach (var clip in clips)
        {
            if (!clip.Automated || clip.SourceSessionPath is null)
                continue;
            if (clip.StartTime is { } start && start > 0
                && (!earliest.TryGetValue(clip.SourceSessionPath, out var current) || start < current))
                earliest[clip.SourceSessionPath] = start;
        }

        return earliest;
    }

    private ContentItem MissingSourceItem(string sourcePath, LibraryState state, RecordingLinks recordings,
        bool highlightsOnly, Dictionary<string, double> earliestLinkedHighlightStart)
    {
        var fileName = ContentLayout.FileNameOf(sourcePath);
        var item = new ContentItem
        {
            ContentType = "recording",
            FileName = fileName,
            FilePath = sourcePath,
            Title = Path.GetFileNameWithoutExtension(fileName),
            FileSizeBytes = 0,
            Bookmarks = [],
            Favorite = false,
            HighlightsOnly = highlightsOnly ? true : null,
            VideoMissing = highlightsOnly ? null : true,
        };
        if (IsActiveRecording(sourcePath, state))
        {
            item.Recording = true;
            ApplyPendingGame(item, state.PendingMetadata);
        }

        var metadata = _metadata.Load(fileName);
        if (metadata is not null)
        {
            ApplyRecordingMetadata(item, metadata);
            recordings.Remember(item, fileName, sourcePath);
        }
        else if (earliestLinkedHighlightStart.TryGetValue(sourcePath, out var sessionStart))
        {
            item.StartTime = sessionStart;
        }

        if (item.Game is null && item.GameId is null
            && ContentLayout.GameSegment(sourcePath) is { } segment
            && _games.FindByIdOrName(segment) is { } known)
        {
            item.GameId = known.Id;
            item.Game = known.Name;
        }

        return item;
    }

    private static bool IsActiveRecording(string relative, LibraryState state) =>
        state.ActiveRecordingPath is not null
        && string.Equals(relative, state.ActiveRecordingPath, PathComparison);

    private void ApplyPendingGame(ContentItem item, RecordingMetadata? pending)
    {
        if (pending is null)
            return;

        item.Game = string.IsNullOrWhiteSpace(pending.Game) ? null : pending.Game;
        item.GameId = _games.ResolveStoredGameId(pending.GameId, item.Game);
    }

    private void BackfillClipGame(ContentItem clip, ClipTitleRecord? record)
    {
        var game = clip.Game;
        var gameId = clip.GameId;

        if (gameId is null && !string.IsNullOrWhiteSpace(game))
            gameId = _games.ResolveLegacyGameId(game);

        if (game is null && gameId is null
            && (ContentLayout.GameSegment(clip.SourceSessionPath) ?? ContentLayout.GameSegment(clip.FilePath)) is { } segment
            && _games.FindByIdOrName(segment) is { } known)
        {
            gameId = known.Id;
            game = known.Name;
        }

        if (game is null && gameId is null)
            return;

        clip.Game = game;
        clip.GameId = gameId;

        if (!string.IsNullOrWhiteSpace(clip.FileName)
            && record is not null
            && string.IsNullOrWhiteSpace(record.Game)
            && string.IsNullOrWhiteSpace(record.GameId))
        {
            _backfill(new LibraryBackfill.ClipGame(Path.GetFileName(clip.FileName), game, gameId));
        }
    }

    private void ApplyRecordingMetadata(ContentItem item, RecordingMetadata metadata)
    {
        item.Bookmarks = MapBookmarks(metadata.Bookmarks);
        item.HasAutomaticClipCandidates = metadata.Bookmarks.Any(AutomaticClipCandidates.Includes);
        item.Title = string.IsNullOrWhiteSpace(metadata.Title) ? item.Title : metadata.Title;
        item.Favorite = metadata.Favorite;
        item.StartTime = UnixSeconds(metadata.StartTime);
        item.Game = string.IsNullOrWhiteSpace(metadata.Game) ? null : metadata.Game;
        item.GameId = _games.ResolveStoredGameId(metadata.GameId, item.Game);
        item.DurationSeconds = metadata.DurationSeconds;
        item.AudioTracks = ToAudioTrackInfo(metadata);
    }

    private static TValue? InheritedFrom<TValue>(ContentItem clip,
        Dictionary<string, TValue> byRecordingPath, Dictionary<string, TValue> bySession)
        where TValue : class
    {
        if (clip.SourceSessionPath is not null)
        {
            if (byRecordingPath.TryGetValue(clip.SourceSessionPath, out var linked))
                return linked;
            if (clip.Automated)
                return null;
        }

        var clipBaseName = Path.GetFileNameWithoutExtension(clip.FileName);
        TValue? inherited = null;
        var matched = 0;

        foreach (var (recording, value) in bySession)
        {
            if (recording.Length <= matched)
                continue;
            if (!clipBaseName.StartsWith(recording, StringComparison.Ordinal))
                continue;
            if (clipBaseName.Length != recording.Length && clipBaseName[recording.Length] != '-')
                continue;

            inherited = value;
            matched = recording.Length;
        }

        return inherited;
    }

    private static List<BookmarkItem> MapBookmarks(IEnumerable<Bookmark> bookmarks) =>
        MapBookmarks(bookmarks.Select(bookmark => (bookmark, bookmark.Time.TotalSeconds)));

    private static List<BookmarkItem> MapBookmarks(IEnumerable<(Bookmark Bookmark, double Time)> placed) => placed
        .Select(entry => new BookmarkItem
        {
            Id = entry.Bookmark.Id.ToString(),
            Type = entry.Bookmark.Type.ToString().ToLowerInvariant(),
            Subtype = entry.Bookmark.Subtype,
            Time = entry.Time,
        })
        .ToList();

    private static List<ClipSourceSpan> SourceSpansOf(ClipTitleRecord record)
    {
        if (record.SourceSpans is { Count: > 0 } spans)
            return spans;
        if (record.ClipStartTime is { } start && record.ClipEndTime is { } end && end > start)
            return [new ClipSourceSpan { Start = start, End = end }];
        return [];
    }

    private static double? LocalTimeIn(double sourceSeconds, IReadOnlyList<ClipSourceSpan> spans)
    {
        var offset = 0d;
        foreach (var span in spans)
        {
            var length = span.End - span.Start;
            if (length <= 0)
                continue;
            if (sourceSeconds >= span.Start && sourceSeconds <= span.End)
                return offset + (sourceSeconds - span.Start);
            offset += length;
        }

        return null;
    }

    private static List<BookmarkItem> InheritedBookmarks(
        IEnumerable<Bookmark> bookmarks, IReadOnlyList<ClipSourceSpan> spans, double? clipDuration)
    {
        var placed = new List<(Bookmark Bookmark, double Time)>();
        foreach (var bookmark in bookmarks)
        {
            if (LocalTimeIn(bookmark.Time.TotalSeconds, spans) is not { } local)
                continue;
            if (local < 0 || (clipDuration is { } duration && duration > 0 && local > duration))
                continue;
            placed.Add((bookmark, local));
        }

        placed.Sort((left, right) => left.Time.CompareTo(right.Time));
        return MapBookmarks(placed);
    }

    private static List<AudioTrackInfo>? ToAudioTrackInfo(RecordingMetadata metadata)
    {
        if (metadata.AudioTracks.Count == 0)
            return null;

        return metadata.AudioTracks
            .OrderBy(track => track.Index)
            .Select(track => new AudioTrackInfo { Index = track.Index, Name = track.Name })
            .ToList();
    }

    private sealed class RecordingLinks
    {
        internal Dictionary<string, string> GamesBySession { get; } = new(PathComparer);
        internal Dictionary<string, string> GameIdsBySession { get; } = new(PathComparer);
        internal Dictionary<string, List<AudioTrackInfo>> TracksBySession { get; } = new(PathComparer);
        internal Dictionary<string, string> GamesByPath { get; } = new(PathComparer);
        internal Dictionary<string, string> GameIdsByPath { get; } = new(PathComparer);
        internal Dictionary<string, List<AudioTrackInfo>> TracksByPath { get; } = new(PathComparer);

        internal void Remember(ContentItem item, string fileName, string wirePath)
        {
            var baseName = Path.GetFileNameWithoutExtension(fileName);
            if (item.Game is not null)
            {
                GamesBySession[baseName] = item.Game;
                GamesByPath[wirePath] = item.Game;
            }
            if (item.GameId is not null)
            {
                GameIdsBySession[baseName] = item.GameId;
                GameIdsByPath[wirePath] = item.GameId;
            }
            if (item.AudioTracks is not null)
            {
                TracksBySession[baseName] = item.AudioTracks;
                TracksByPath[wirePath] = item.AudioTracks;
            }
        }
    }
}
