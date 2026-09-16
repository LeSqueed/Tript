// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Serilog;
using Tript.App.Content;
using Tript.Core;

namespace Tript.App;

internal sealed partial class AppHost
{
    internal void DeleteContent(DeleteContentParameters? parameters)
    {
        if (parameters is null || string.IsNullOrEmpty(parameters.FileName))
            return;

        DeleteItems([parameters], permanent: false);
        PushContent();
        PushTrash();
    }

    internal void DeleteMultipleContent(DeleteMultipleContentParameters? parameters)
    {
        if (parameters?.Items is null)
            return;

        DeleteItems(parameters.Items, parameters.Permanent);

        PushContent();
        PushTrash();
    }

    private void DeleteItems(IReadOnlyList<DeleteContentParameters> items, bool permanent)
    {
        var processed = new HashSet<string>(ContentPathComparer);
        var clipRecords = _clipTitles.EnumerateRecords();
        var referencedSources = ReferencedSourcePaths(clipRecords);

        foreach (var item in items)
        {
            if (!string.IsNullOrEmpty(item.FileName))
                DeleteOne(item, permanent || item.Permanent, clipRecords, referencedSources, processed);
        }

        ReleaseSourceMetadataOfDeletedClips(clipRecords);
    }

    private HashSet<string> ReferencedSourcePaths(
        IReadOnlyList<(string ClipFileName, ClipTitleRecord Record)> clipRecords)
    {
        var referenced = new HashSet<string>(ContentPathComparer);
        foreach (var (_, record) in clipRecords)
        {
            if (NormalizeSourcePath(record.SourceSessionPath) is { } path)
                referenced.Add(path);
        }

        return referenced;
    }

    private void ReleaseSourceMetadataOfDeletedClips(
        IReadOnlyList<(string ClipFileName, ClipTitleRecord Record)> clipRecordsBeforeDelete)
    {
        try
        {
            var survivingClips = _clipTitles.EnumerateRecords();
            var surviving = new HashSet<string>(
                survivingClips.Select(entry => entry.ClipFileName), ContentPathComparer);

            var candidates = new HashSet<string>(ContentPathComparer);
            foreach (var (clipFileName, record) in clipRecordsBeforeDelete)
            {
                if (surviving.Contains(clipFileName))
                    continue;
                if (NormalizeSourcePath(record.SourceSessionPath) is { } path)
                    candidates.Add(path);
            }

            if (candidates.Count == 0)
                return;

            var trashed = _trash.List();
            if (trashed.Any(entry => entry.ContentType is "clip" or "highlight"))
                return;

            var trashedNames = new HashSet<string>(
                trashed.Select(entry => entry.FileName), ContentPathComparer);
            var stillReferenced = ReferencedSourcePaths(survivingClips);

            foreach (var path in candidates)
            {
                if (stillReferenced.Contains(path))
                    continue;

                var fileName = ContentLayout.FileNameOf(path);
                if (trashedNames.Contains(fileName))
                    continue;

                var video = _content.ResolveWithinRoot(path);
                if (video is not null && File.Exists(video))
                    continue;

                _metadata.Delete(fileName);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warning("could not release source metadata of deleted clips: {Reason}", exception.Message);
        }
    }

    private void DeleteOne(DeleteContentParameters item, bool permanent,
        IReadOnlyList<(string ClipFileName, ClipTitleRecord Record)> clipRecords,
        HashSet<string> referencedSources, HashSet<string> processed, ClipTitleRecord? enumeratedClipRecord = null)
    {
        var target = _content.ResolveWithinRoot(item.FileName);
        if (target is null || !processed.Add(target))
            return;

        var fileName = Path.GetFileName(target);
        var relative = RelativeToRoot(target);
        var contentType = ResolveContentType(item.ContentType, relative);
        if (item.DeleteLinkedHighlights && contentType == "recording"
            && !ContentLayout.IsClipPath(relative))
        {
            DeleteLinkedAutomaticHighlights(target, relative, permanent, clipRecords, referencedSources, processed);
        }

        var keepMetadataForClips = contentType == "recording" && referencedSources.Contains(relative);

        if (permanent)
        {
            UnlinkContent(target, fileName, keepMetadataForClips);
            return;
        }

        var seed = new TrashEntryRecord
        {
            ContentType = contentType,
            FileName = fileName,
            OriginalPath = relative,
            DeletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

        var metadata = _metadata.Load(fileName);
        if (metadata is not null)
        {
            seed.Title = string.IsNullOrWhiteSpace(metadata.Title) ? null : metadata.Title;
            seed.Game = string.IsNullOrWhiteSpace(metadata.Game) ? null : metadata.Game;
            seed.DurationSeconds = metadata.DurationSeconds;
        }

        var clipRecord = enumeratedClipRecord ?? _clipTitles.LoadRecord(fileName);
        if (clipRecord is not null)
        {
            seed.Title ??= string.IsNullOrWhiteSpace(clipRecord.Title) ? null : clipRecord.Title;
            seed.DurationSeconds ??= clipRecord.DurationSeconds;
        }

        seed.Title ??= Path.GetFileNameWithoutExtension(fileName);

        var files = new List<TrashedFile>();
        if (File.Exists(target))
        {
            seed.FileSizeBytes = ContentCatalogue.SafeLength(new FileInfo(target));
            files.Add(new TrashedFile(target, relative));
        }

        if (!keepMetadataForClips)
            AddIfPresent(files, _metadata.PathFor(fileName));
        AddIfPresent(files, _clipTitles.PathFor(fileName));

        using var thumbnailHold = _thumbnails.HoldForRemoval(fileName, ThumbnailStore.ExtractionReleaseWait);
        AddIfPresent(files, _thumbnails.PathFor(fileName));

        if (files.Count == 0)
            return;

        if (_trash.Add(files, seed, out var failure) is null)
        {
            Log.Warning("could not move {Target} to the trash: {Failure}", target, failure);
            PushError($"'{fileName}' could not be moved to the trash ({failure}), so it was left where it is.");
        }
        else
        {
            _thumbnails.Delete(fileName);
        }
    }

    private void DeleteLinkedAutomaticHighlights(string sourceTarget, string sourcePath, bool permanent,
        IReadOnlyList<(string ClipFileName, ClipTitleRecord Record)> clipRecords,
        HashSet<string> referencedSources, HashSet<string> processed)
    {
        var highlightsDirectory = HighlightsDirectoryPathForSource(sourceTarget);
        foreach (var (clipFileName, record) in clipRecords)
        {
            if (!record.IsAutomatic || record.Favorite || string.IsNullOrWhiteSpace(record.SourceSessionPath))
                continue;
            if (!string.Equals(NormalizeSourcePath(record.SourceSessionPath), sourcePath,
                ContentPathComparison))
                continue;

            DeleteOne(new DeleteContentParameters
            {
                ContentType = "highlight",
                FileName = RelativeToRoot(Path.Combine(highlightsDirectory, clipFileName)),
            }, permanent, clipRecords, referencedSources, processed, record);
        }
    }

    private void UnlinkContent(string target, string fileName, bool keepMetadataForClips = false)
    {
        using var thumbnailHold = _thumbnails.HoldForRemoval(fileName, ThumbnailStore.ExtractionReleaseWait);
        try
        {
            SharingViolationRetry.Run(() => File.Delete(target));

            var metadataDeleted = keepMetadataForClips || _metadata.Delete(fileName);
            var clipRecordDeleted = _clipTitles.Delete(fileName);
            var thumbnailDeleted = _thumbnails.Delete(fileName);
            if (!metadataDeleted || !clipRecordDeleted || !thumbnailDeleted)
            {
                PushError(
                    $"'{fileName}' was deleted, but one or more associated records could not be removed. " +
                    "Check the metadata and thumbnail folders before reusing the name.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warning("could not delete {Target}: {Reason}", target, exception.Message);
            PushError($"'{fileName}' could not be deleted ({exception.Message}).");
        }
    }

    private void AddIfPresent(List<TrashedFile> files, string path)
    {
        if (File.Exists(path))
            files.Add(new TrashedFile(path, RelativeToRoot(path)));
    }

    private static readonly HashSet<string> WireContentTypes =
        new(StringComparer.Ordinal) { "recording", "clip", "highlight", "buffer" };

    private static string ResolveContentType(string? requested, string relativePath)
    {
        if (requested is not null && WireContentTypes.Contains(requested))
            return requested;
        return ContentLayout.IsClipPath(relativePath) ? "clip" : "recording";
    }
}
