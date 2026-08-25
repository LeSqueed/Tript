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

    internal void PushContent()
    {
        _ipc.Broadcast("content", JsonSerializer.SerializeToElement(new
        {
            content = ListContent(),
        }, Wire.Options));
    }

    private static bool IsUsableOffsetSeconds(double seconds) =>
        double.IsFinite(seconds) && seconds >= 0 && seconds < TimeSpan.MaxValue.TotalSeconds;

    private void PushError(string message)
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
        var items = new List<ContentItem>();
        var root = new DirectoryInfo(EffectiveRoot);
        if (!root.Exists)
            return items;

        var gamesByRecording = new Dictionary<string, string>(StringComparer.Ordinal);
        var gameIdsByRecording = new Dictionary<string, string>(StringComparer.Ordinal);
        var tracksByRecording = new Dictionary<string, List<AudioTrackInfo>>(StringComparer.Ordinal);
        var gamesByRecordingPath = new Dictionary<string, string>(StringComparer.Ordinal);
        var gameIdsByRecordingPath = new Dictionary<string, string>(StringComparer.Ordinal);
        var tracksByRecordingPath = new Dictionary<string, List<AudioTrackInfo>>(StringComparer.Ordinal);
        var clips = new List<ContentItem>();
        var probeBudget = DurationProbeBudget;
        string? processingSessionPath;
        bool processingPaused = false;
        int? processingCompleted = null;
        int? processingTotal = null;
        lock (_automaticClipGate)
        {
            processingSessionPath = _automaticClipJob?.SourceSessionPath;
            processingPaused = _automaticClipJob?.Paused == true;
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
                var metadata = _metadata.Load(file.Name);
                if (metadata is not null)
                {
                    item.Bookmarks = metadata.Bookmarks
                        .Select(bookmark => new BookmarkItem
                        {
                            Id = bookmark.Id.ToString(),
                            Type = bookmark.Type.ToString().ToLowerInvariant(),
                            Subtype = bookmark.Subtype,
                            Time = bookmark.Time.TotalSeconds,
                        })
                        .ToList();
                    item.Title = string.IsNullOrWhiteSpace(metadata.Title) ? item.Title : metadata.Title;
                    item.Favorite = metadata.Favorite;
                    item.StartTime = DateTimeToUnixSeconds(metadata.StartTime);
                    item.Game = string.IsNullOrWhiteSpace(metadata.Game) ? null : metadata.Game;
                    item.GameId = string.IsNullOrWhiteSpace(metadata.GameId)
                        ? ResolveLegacyGameId(item.Game)
                        : metadata.GameId;
                    item.DurationSeconds = metadata.DurationSeconds;
                    item.AudioTracks = ToAudioTrackInfo(metadata);

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
            }
            else
            {
                var record = _clipTitles.LoadRecord(file.Name);
                item.SourceSessionPath = record?.SourceSessionPath;
                item.IsHdr = record?.IsHdr;
                if (record?.IsAutomatic == true)
                {
                    contentType = "highlight";
                    item.ContentType = contentType;
                    item.Automated = true;
                    item.SourceSessionPath = record.SourceSessionPath;
                    item.ClipStartTime = record.ClipStartTime;
                    item.ClipEndTime = record.ClipEndTime;
                }
                if (!string.IsNullOrWhiteSpace(record?.Title))
                    item.Title = record.Title;
                item.Favorite = record?.Favorite ?? false;
                item.DurationSeconds = record?.DurationSeconds;
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
                    Console.Error.WriteLine($"Tript.App: could not read HDR metadata of '{relative}': {exception.Message}");
                    MarkUnprobeable(file.FullName);
                }
            }

            items.Add(item);
        }

        foreach (var clip in clips)
        {
            clip.Game = InheritedGame(clip, gamesByRecordingPath, gamesByRecording);
            clip.GameId = InheritedFrom(clip, gameIdsByRecordingPath, gameIdsByRecording);
            clip.AudioTracks = InheritedFrom(clip, tracksByRecordingPath, tracksByRecording);
        }

        items.Sort((left, right) =>
        {
            var byDate = (right.StartTime ?? 0).CompareTo(left.StartTime ?? 0);
            return byDate != 0 ? byDate : string.CompareOrdinal(left.FilePath, right.FilePath);
        });

        return items;
    }

    private static string? InheritedGame(ContentItem clip,
        Dictionary<string, string> gamesByRecordingPath, Dictionary<string, string> gamesByRecording)
        => InheritedFrom(clip, gamesByRecordingPath, gamesByRecording);

    private static TValue? InheritedFrom<TValue>(ContentItem clip,
        Dictionary<string, TValue> byRecordingPath, Dictionary<string, TValue> bySession)
        where TValue : class
    {
        if (clip.SourceSessionPath is not null && byRecordingPath.TryGetValue(clip.SourceSessionPath, out var linked))
            return linked;

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
            Console.Error.WriteLine($"Tript.App: could not read the duration of '{relativePath}': {exception.Message}");
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
