// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Tript.App.Content;
using Tript.App.Ipc;
using Tript.Core;
using Tript.Media;
using Tript.Settings;
using Serilog;

namespace Tript.App;

internal sealed partial class AppHost
{
    private const int DurationProbeBudget = 12;

    internal string ContentRoot => EffectiveRoot;

    internal void OpenFileLocation(OpenFileLocationParameters? parameters)
    {
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.FilePath))
            return;

        var path = ContentServer.ResolveWithinRoot(EffectiveRoot, parameters.FilePath);
        if (path is null || !File.Exists(path))
        {
            PushError("That file is not inside the recording folder or no longer exists.");
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path.Replace("\"", string.Empty)}\"",
                    UseShellExecute = true,
                });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", $"-R \"{path.Replace("\"", string.Empty)}\"");
            }
            else
            {
                Process.Start("xdg-open", Path.GetDirectoryName(path)!);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            PushError($"The file location could not be opened: {exception.Message}");
        }
    }

    internal void OpenInBrowser(OpenInBrowserParameters? parameters)
    {
        var url = parameters?.Url;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            Log.Warning(exception, "AppHost: could not open {Url} in a browser", url);
        }
    }

    internal void PushContent()
    {
        _ipc.Broadcast("content", JsonSerializer.SerializeToElement(new
        {
            content = ListContent(),
        }, Wire.Options));
    }

    private static bool IsUsableOffsetSeconds(double seconds) =>
        double.IsFinite(seconds) && seconds >= 0 && seconds < TimeSpan.MaxValue.TotalSeconds;

    internal void PushError(string message)
    {
        _ipc.Broadcast("error", JsonSerializer.SerializeToElement(new
        {
            message,
        }, Wire.Options));
        RequestNotification(NotificationKind.Error, "Tript error", message);
    }

    private void RequestNotification(NotificationKind kind, string title, string body) =>
        NotificationRequested?.Invoke(kind, title, body);

    private void PushWarning(string? message)
    {
        _ipc.Broadcast("warning", JsonSerializer.SerializeToElement(
            message is null ? null : new { message }, Wire.Options));
    }

    internal List<ContentItem> ListContent()
    {
        var pathComparer = ContentPathComparer;
        var clipRecords = _clipTitles.EnumerateRecords()
            .ToDictionary(entry => entry.ClipFileName, entry => entry.Record, pathComparer);
        var items = new List<ContentItem>();
        var root = new DirectoryInfo(EffectiveRoot);
        if (!root.Exists)
            return items;

        var activeRecordingPath = IsRecording && _activeSessionPath is { Length: > 0 }
            ? Path.GetRelativePath(EffectiveRoot, _activeSessionPath).Replace(Path.DirectorySeparatorChar, '/')
            : null;

        var gamesByRecording = new Dictionary<string, string>(pathComparer);
        var gameIdsByRecording = new Dictionary<string, string>(pathComparer);
        var tracksByRecording = new Dictionary<string, List<AudioTrackInfo>>(pathComparer);
        var gamesByRecordingPath = new Dictionary<string, string>(pathComparer);
        var gameIdsByRecordingPath = new Dictionary<string, string>(pathComparer);
        var tracksByRecordingPath = new Dictionary<string, List<AudioTrackInfo>>(pathComparer);
        var clips = new List<ContentItem>();
        var linkedAutomaticSources = new HashSet<string>(pathComparer);
        var highlightsOnlySources = new Dictionary<string, bool>(pathComparer);
        var earliestLinkedHighlightStart = new Dictionary<string, double>(pathComparer);
        var recordingPaths = new HashSet<string>(pathComparer);
        var probeBudget = DurationProbeBudget;
        string? processingSessionPath;
        bool processingPaused = false;
        int? processingCompleted = null;
        int? processingTotal = null;
        lock (_automaticClipGate)
        {
            processingSessionPath = _automaticClipJob?.SourceSessionPath;
            processingPaused = _automaticClipJob is not null
                && (_automaticClipJob.PausedByUser || _backgroundWorkSuspendedForRecording);
            processingCompleted = _automaticClipJob?.Completed;
            processingTotal = _automaticClipJob?.Total;
        }

        foreach (var file in root.EnumerateFiles("*.mp4", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(EffectiveRoot, file.FullName)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (IsTrashPath(relative))
                continue;

            var topLevel = TopLevelDirectory(relative);
            var contentType = topLevel is "clips" or "highlights" ? "clip" : "recording";

            var item = new ContentItem
            {
                ContentType = contentType,
                FileName = file.Name,
                FilePath = relative,
                Title = Path.GetFileNameWithoutExtension(file.Name),
                FileSizeBytes = SafeLength(file),
                AutomaticClipsProcessing = string.Equals(relative, processingSessionPath,
                    StringComparison.OrdinalIgnoreCase),
                AutomaticClipsPaused = string.Equals(relative, processingSessionPath,
                    StringComparison.OrdinalIgnoreCase) && processingPaused,
                AutomaticClipsCompleted = string.Equals(relative, processingSessionPath,
                    StringComparison.OrdinalIgnoreCase) ? processingCompleted : null,
                AutomaticClipsTotal = string.Equals(relative, processingSessionPath,
                    StringComparison.OrdinalIgnoreCase) ? processingTotal : null,
            };

            if (contentType == "recording")
            {
                recordingPaths.Add(relative);
                var metadata = _metadata.Load(file.Name);
                if (metadata is not null)
                {
                    ApplyRecordingMetadata(item, metadata);

                    var baseName = Path.GetFileNameWithoutExtension(file.Name);
                    if (item.Game is not null)
                    {
                        gamesByRecording[baseName] = item.Game;
                        gamesByRecordingPath[relative] = item.Game;
                    }
                    if (item.GameId is not null)
                    {
                        gameIdsByRecording[baseName] = item.GameId;
                        gameIdsByRecordingPath[relative] = item.GameId;
                    }
                    if (item.AudioTracks is not null)
                    {
                        tracksByRecording[baseName] = item.AudioTracks;
                        tracksByRecordingPath[relative] = item.AudioTracks;
                    }
                }
                else
                {
                    item.Bookmarks = [];
                }

                if (activeRecordingPath is not null
                    && string.Equals(relative, activeRecordingPath, ContentPathComparison))
                {
                    item.Recording = true;
                    if (_pendingMetadata is not null)
                    {
                        item.Game = string.IsNullOrWhiteSpace(_pendingMetadata.Game)
                            ? null
                            : _pendingMetadata.Game;
                        item.GameId = ResolveStoredGameId(_pendingMetadata.GameId, item.Game);
                    }
                    item.Bookmarks = MapBookmarks(_sessionTracker.Active?.Bookmarks ?? []);
                }
            }
            else
            {
                clipRecords.TryGetValue(file.Name, out var record);
                item.SourceSessionPath = NormalizeSourcePath(record?.SourceSessionPath);
                item.IsHdr = record?.IsHdr;
                item.ClipStartTime = record?.ClipStartTime;
                item.ClipEndTime = record?.ClipEndTime;
                if (record?.IsAutomatic == true)
                {
                    contentType = "highlight";
                    item.ContentType = contentType;
                    item.Automated = true;
                    if (item.SourceSessionPath is not null)
                    {
                        linkedAutomaticSources.Add(item.SourceSessionPath);
                        var sourceIsHighlightsOnly = record.SourceSessionHighlightsOnly == true;
                        highlightsOnlySources[item.SourceSessionPath] = highlightsOnlySources.TryGetValue(
                            item.SourceSessionPath, out var current)
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
                    item.GameId = ResolveStoredGameId(record.GameId, item.Game);
                }
                clips.Add(item);
            }

            item.StartTime ??= DateTimeToUnixSeconds(file.LastWriteTime);

            if (item.DurationSeconds is null && probeBudget > 0 && !IsUnprobeable(file.FullName))
            {
                probeBudget--;
                item.DurationSeconds = TryReadDuration(file, relative, contentType == "recording");
            }

            if (contentType is "clip" or "highlight" && item.IsHdr is null
                && probeBudget > 0 && !IsUnprobeable(file.FullName) && LibraryProbe is { } probe)
            {
                probeBudget--;
                try
                {
                    item.IsHdr = probe.Probe(file.FullName).IsHdr;
                    _clipTitles.SaveHdrStatus(file.Name, item.IsHdr.Value);
                }
                catch (Exception exception)
                {
                    Log.Warning("could not read HDR metadata of {Path}: {Reason}", relative, exception.Message);
                    MarkUnprobeable(file.FullName);
                }
            }

            items.Add(item);
        }

        var sourceMetadata = new Dictionary<string, RecordingMetadata?>(pathComparer);
        foreach (var clip in clips)
        {
            if (clip.SourceSessionPath is null || !clipRecords.TryGetValue(clip.FileName, out var record))
                continue;

            var spans = SourceSpansOf(record);
            if (spans.Count == 0)
                continue;

            var sourceFileName = FileNameFromWirePath(clip.SourceSessionPath);
            if (!sourceMetadata.TryGetValue(sourceFileName, out var metadata))
            {
                metadata = _metadata.Load(sourceFileName);
                sourceMetadata[sourceFileName] = metadata;
            }
            if (metadata is null)
                continue;

            clip.Bookmarks = InheritedBookmarks(metadata.Bookmarks, spans, clip.DurationSeconds);
        }

        foreach (var clip in clips)
        {
            if (!clip.Automated || clip.SourceSessionPath is null)
                continue;
            if (clip.StartTime is { } start && start > 0
                && (!earliestLinkedHighlightStart.TryGetValue(clip.SourceSessionPath, out var earliest)
                    || start < earliest))
                earliestLinkedHighlightStart[clip.SourceSessionPath] = start;
        }

        foreach (var sourcePath in linkedAutomaticSources)
        {
            if (recordingPaths.Contains(sourcePath))
                continue;

            var fileName = FileNameFromWirePath(sourcePath);
            var item = new ContentItem
            {
                ContentType = "recording",
                FileName = fileName,
                FilePath = sourcePath,
                Title = Path.GetFileNameWithoutExtension(fileName),
                FileSizeBytes = 0,
                Bookmarks = [],
                Favorite = false,
            };
            var highlightsOnly = highlightsOnlySources.TryGetValue(sourcePath, out var marked) && marked;
            item.HighlightsOnly = highlightsOnly ? true : null;
            item.VideoMissing = highlightsOnly ? null : true;
            if (activeRecordingPath is not null
                && string.Equals(sourcePath, activeRecordingPath, ContentPathComparison))
            {
                item.Recording = true;
                if (_pendingMetadata is not null)
                {
                    item.Game = string.IsNullOrWhiteSpace(_pendingMetadata.Game) ? null : _pendingMetadata.Game;
                    item.GameId = ResolveStoredGameId(_pendingMetadata.GameId, item.Game);
                }
            }
            var metadata = _metadata.Load(fileName);
            if (metadata is not null)
            {
                ApplyRecordingMetadata(item, metadata);

                var baseName = Path.GetFileNameWithoutExtension(fileName);
                if (item.Game is not null)
                {
                    gamesByRecording[baseName] = item.Game;
                    gamesByRecordingPath[sourcePath] = item.Game;
                }
                if (item.GameId is not null)
                {
                    gameIdsByRecording[baseName] = item.GameId;
                    gameIdsByRecordingPath[sourcePath] = item.GameId;
                }
                if (item.AudioTracks is not null)
                {
                    tracksByRecording[baseName] = item.AudioTracks;
                    tracksByRecordingPath[sourcePath] = item.AudioTracks;
                }
            }
            else if (earliestLinkedHighlightStart.TryGetValue(sourcePath, out var sessionStart))
            {
                item.StartTime = sessionStart;
            }

            if (item.Game is null && item.GameId is null
                && GameSegmentFromPath(sourcePath) is { } sessionSegment)
            {
                var known = GameList.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, sessionSegment, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candidate.Name, sessionSegment, StringComparison.OrdinalIgnoreCase));
                if (known is not null)
                {
                    item.GameId = known.Id;
                    item.Game = known.Name;
                }
            }

            items.Add(item);
        }

        foreach (var clip in clips)
        {
            if (clip.Game is null && clip.GameId is null)
            {
                clip.Game = InheritedGame(clip, gamesByRecordingPath, gamesByRecording);
                clip.GameId = InheritedFrom(clip, gameIdsByRecordingPath, gameIdsByRecording);
            }
            clipRecords.TryGetValue(clip.FileName, out var record);
            BackfillClipGame(clip, record);
            clip.AudioTracks = InheritedFrom(clip, tracksByRecordingPath, tracksByRecording);
        }

        foreach (var item in items)
            item.Game = ResolveLibraryGameName(item.Game, item.GameId);

        items.Sort((left, right) =>
        {
            var byDate = (right.StartTime ?? 0).CompareTo(left.StartTime ?? 0);
            return byDate != 0 ? byDate : string.CompareOrdinal(left.FilePath, right.FilePath);
        });

        return items;
    }

    private string? ResolveLibraryGameName(string? storedName, string? gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId))
            return storedName;

        foreach (var game in GameList)
        {
            if (!string.Equals(game.Id, gameId, StringComparison.OrdinalIgnoreCase))
                continue;
            return string.IsNullOrWhiteSpace(game.Name) ? storedName : game.Name;
        }

        return storedName;
    }

    private static string? InheritedGame(ContentItem clip,
        Dictionary<string, string> gamesByRecordingPath, Dictionary<string, string> gamesByRecording)
        => InheritedFrom(clip, gamesByRecordingPath, gamesByRecording);

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

        var clipFileName = clip.FileName;
        var clipBaseName = Path.GetFileNameWithoutExtension(clipFileName);
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

    private static string? GameSegmentFromPath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        var segment = relativePath.Split(new[] { '/', '\\' }, 2)[0];
        return segment is "sessions" or "clips" or "highlights" or "metadata" or ".trash"
            ? null
            : segment;
    }

    private void BackfillClipGame(ContentItem clip, ClipTitleRecord? record)
    {
        var game = clip.Game;
        var gameId = clip.GameId;

        if (gameId is null && !string.IsNullOrWhiteSpace(game))
            gameId = ResolveLegacyGameId(game);

        if (game is null && gameId is null)
        {
            var segment = GameSegmentFromPath(clip.SourceSessionPath)
                ?? GameSegmentFromPath(clip.FilePath);
            if (segment is not null)
            {
                var known = GameList.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, segment, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candidate.Name, segment, StringComparison.OrdinalIgnoreCase));
                if (known is not null)
                {
                    gameId = known.Id;
                    game = known.Name;
                }
            }
        }

        if (game is null && gameId is null)
            return;

        clip.Game = game;
        clip.GameId = gameId;

        var fileName = string.IsNullOrWhiteSpace(clip.FileName)
            ? null
            : Path.GetFileName(clip.FileName);
        if (fileName is null)
            return;

        if (record is not null && string.IsNullOrWhiteSpace(record.Game) && string.IsNullOrWhiteSpace(record.GameId))
            _clipTitles.SaveGame(fileName, game, gameId);
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

    private void ApplyRecordingMetadata(ContentItem item, RecordingMetadata metadata)
    {
        item.Bookmarks = MapBookmarks(metadata.Bookmarks);
        item.HasAutomaticClipCandidates = metadata.Bookmarks.Any(IsAutomaticClipCandidate);
        item.Title = string.IsNullOrWhiteSpace(metadata.Title) ? item.Title : metadata.Title;
        item.Favorite = metadata.Favorite;
        item.StartTime = DateTimeToUnixSeconds(metadata.StartTime);
        item.Game = string.IsNullOrWhiteSpace(metadata.Game) ? null : metadata.Game;
        item.GameId = ResolveStoredGameId(metadata.GameId, item.Game);
        item.DurationSeconds = metadata.DurationSeconds;
        item.AudioTracks = ToAudioTrackInfo(metadata);
    }

    private string? NormalizeSourcePath(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return null;

        var wirePath = sourcePath.Trim().Replace('\\', '/');
        var absolutePath = ContentServer.ResolveWithinRoot(EffectiveRoot, wirePath);
        if (absolutePath is null)
            return null;

        return Path.GetRelativePath(EffectiveRoot, absolutePath)
            .Replace(Path.DirectorySeparatorChar, '/');
    }

    private static StringComparer ContentPathComparer => FilePaths.Comparer;

    private static StringComparison ContentPathComparison => FilePaths.Comparison;

    private static string FileNameFromWirePath(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator >= 0 ? path[(separator + 1)..] : path;
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

    private double? TryReadDuration(FileInfo file, string relativePath, bool isRecording)
    {
        var probe = LibraryProbe;
        if (probe is null)
            return null;

        double seconds;
        try
        {
            seconds = probe.Probe(file.FullName).DurationSeconds;
        }
        catch (Exception exception)
        {
            Log.Warning("could not read the duration of {Path}: {Reason}", relativePath, exception.Message);
            MarkUnprobeable(file.FullName);
            return null;
        }

        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0)
        {
            MarkUnprobeable(file.FullName);
            return null;
        }

        if (isRecording)
            _metadata.SaveDuration(file.Name, relativePath, seconds);
        else
            _clipTitles.SaveDuration(file.Name, seconds);

        return seconds;
    }

    private void MarkUnprobeable(string absolutePath)
    {
        lock (_unprobeable)
            _unprobeable.Add(absolutePath);
    }

    private bool IsUnprobeable(string absolutePath)
    {
        lock (_unprobeable)
            return _unprobeable.Contains(absolutePath);
    }

    private MediaProbe? LibraryProbe
    {
        get
        {
            var tools = _libraryTools.Value;
            if (tools is null)
                return null;

            return _libraryProbe ??= new MediaProbe(tools.Value.Ffprobe);
        }
    }

    private static long SafeLength(FileInfo file)
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

    private static string TopLevelDirectory(string relativePath)
    {
        foreach (var segment in relativePath.Split('/'))
        {
            if (segment.Equals("sessions", StringComparison.Ordinal)
                || segment.Equals("clips", StringComparison.Ordinal)
                || segment.Equals("highlights", StringComparison.Ordinal))
                return segment;
        }

        var separator = relativePath.IndexOf('/');
        return separator >= 0 ? relativePath[..separator] : relativePath;
    }

    private static bool IsTrashPath(string relativePath) =>
        relativePath.StartsWith(TrashStore.DirectoryName + "/", StringComparison.Ordinal);

    private static double? DateTimeToUnixSeconds(DateTime dateTime)
        => dateTime == default ? null : new DateTimeOffset(dateTime).ToUnixTimeSeconds();
}
