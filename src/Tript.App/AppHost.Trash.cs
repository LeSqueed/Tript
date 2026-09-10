// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using System.Text.Json.Nodes;
using System.ComponentModel;
using System.Diagnostics;
using Serilog;
using Tript.App.Content;
using Tript.App.Ipc;
using Tript.App.Models;
using Tript.Core;
using Tript.Detection;
using Tript.GameDiscovery;
using Tript.Media;
using Tript.Obs;
using Tript.Recorder;
using Tript.Settings;
#if TRIPT_TRAINING
using Tript.App.Training;
#endif
using RecorderStateMachine = Tript.Recorder.Recorder;
using SettingsModel = Tript.Settings.Settings;

namespace Tript.App;

internal sealed partial class AppHost
{
    private int RetentionHours => _settingsStore.Load().Recording.TrashRetentionHours;

    internal List<TrashEntry> TrashEntries()
    {
        var retentionHours = RetentionHours;
        return _trash.List().Select(entry => new TrashEntry
        {
            Id = entry.Id,
            ContentType = entry.ContentType,
            FileName = entry.FileName,
            Title = entry.Title,
            Game = entry.Game,
            DurationSeconds = entry.DurationSeconds,
            FileSizeBytes = entry.FileSizeBytes,
            DeletedAt = entry.DeletedAt,
            PurgeAt = retentionHours <= 0 ? 0 : entry.DeletedAt + retentionHours * 3600L,
        }).ToList();
    }

    internal void PushTrash()
    {
        _ipc.Broadcast("trash", JsonSerializer.SerializeToElement(new
        {
            entries = TrashEntries(),
            retentionHours = RetentionHours,
        }, Wire.Options));
    }

    internal void RestoreTrash(RestoreTrashParameters? parameters)
    {
        if (parameters?.EntryIds is null)
            return;

        foreach (var entryId in parameters.EntryIds)
        {
            var result = _trash.Restore(entryId, EffectiveRoot);
            if (result.Failure is not null)
            {
                Log.Warning("could not restore {EntryId}: {Failure}", entryId, result.Failure);
                PushError($"That item could not be restored ({result.Failure}).");
                continue;
            }

            if (result.Renamed)
            {
                if (RelinkRestoredMetadata(result.FileName!, result.RestoredAs!))
                    PushError($"'{result.FileName}' was restored as '{result.RestoredAs}' — a file with its own name was already there.");
                else
                    PushError($"'{result.FileName}' was restored as '{result.RestoredAs}', but its metadata link could not be updated. Check the metadata folder.");
            }

            if (result.KeptInTrash > 0)
            {
                PushError(
                    $"'{result.FileName}' was restored, but {result.KeptInTrash} of its saved records " +
                    "could not be put back and are still in the trash.");
            }
        }

        PushContent();
        PushTrash();
    }

    private bool RelinkRestoredMetadata(string originalFileName, string restoredFileName)
    {
        lock (_metadata.WriteGate)
        {
        var existing = _metadata.Read(restoredFileName);
        if (existing.State != StoredRecordState.Loaded)
            return existing.State == StoredRecordState.Absent;

        var record = existing.Record!;
        if (!string.Equals(Path.GetFileName(record.VideoPath), originalFileName, StringComparison.OrdinalIgnoreCase))
            return false;
        var separator = record.VideoPath.LastIndexOf('/');
        record.VideoPath = separator < 0
            ? restoredFileName
            : $"{record.VideoPath[..separator]}/{restoredFileName}";
        return _metadata.Save(record);
        }
    }

    internal void PurgeTrash(PurgeTrashParameters? parameters)
    {
        if (parameters is null)
            return;

        var entryIds = parameters.EntryIds ?? _trash.List().Select(entry => entry.Id).ToList();

        foreach (var entryId in entryIds)
        {
            if (_trash.Purge(entryId, out var failure))
                continue;
            Log.Warning("could not purge {EntryId}: {Failure}", entryId, failure);
            PushError($"That item could not be removed from the trash ({failure}).");
        }

        PushContent();
        PushTrash();
    }

    internal void PurgeExpiredTrash()
    {
        try
        {
            var retentionHours = RetentionHours;
            if (retentionHours <= 0)
                return;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var purged = 0;
            foreach (var entry in _trash.List())
            {
                if (entry.DeletedAt + retentionHours * 3600L > now)
                    continue;
                if (_trash.Purge(entry.Id, out _))
                    purged++;
            }

            if (purged == 0)
                return;

            PushContent();
            PushTrash();
        }
        catch (Exception exception)
        {
            Log.Warning("the trash could not be swept: {Reason}", exception.Message);
        }
    }

    internal void RenameContent(RenameContentParameters? parameters)
    {
        if (parameters is null || string.IsNullOrEmpty(parameters.FileName))
            return;

        var target = ResolveContentFile(parameters.FileName);
        if (target is null)
            return;

        var relative = Path.GetRelativePath(EffectiveRoot, target).Replace(Path.DirectorySeparatorChar, '/');
        var fileName = Path.GetFileName(target);

        if (TopLevelDirectory(relative) is "clips" or "highlights")
        {
            RenameClip(fileName, parameters.Title);
            return;
        }

        lock (_metadata.WriteGate)
        {
        var existing = _metadata.Read(fileName);
        if (existing.MustNotBeOverwritten)
        {
            Log.Warning("{FileName} has a metadata record that could not be read ({Failure}); the rename is refused rather than replacing it.", fileName, existing.Failure);
            PushError(
                "The recording title could not be saved — this recording's metadata record could not be read, and overwriting it would lose its game and bookmarks.");
            return;
        }

        var metadata = existing.Record ?? new RecordingMetadata
        {
            VideoPath = relative,
        };
        metadata.Title = parameters.Title;

        if (!_metadata.Save(metadata))
        {
            PushError("The recording title could not be saved — check the recording folder is writable.");
            return;
        }
        PushContent();
        }
    }

    private void RenameClip(string fileName, string title)
    {
        lock (_clipTitles.WriteGate)
        {
        var existing = _clipTitles.Read(fileName);
        if (existing.MustNotBeOverwritten)
        {
            Log.Warning("{FileName} has a clip record that could not be read ({Failure}); the rename is refused rather than replacing it.", fileName, existing.Failure);
            PushError(
                "The clip title could not be saved — this clip's record could not be read, and overwriting it would lose what else is on it.");
            return;
        }

        if (!_clipTitles.Save(fileName, title))
        {
            PushError("The clip title could not be saved — check the recording folder is writable.");
            return;
        }

        PushContent();
        }
    }

    internal void ToggleFavorite(ToggleFavoriteParameters? parameters)
    {
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.FilePath))
            return;

        var target = ResolveContentFile(parameters.FilePath);
        if (target is null)
            return;

        var fileName = Path.GetFileName(target);
        var relative = Path.GetRelativePath(EffectiveRoot, target).Replace(Path.DirectorySeparatorChar, '/');
        if (TopLevelDirectory(relative) is "clips" or "highlights")
        {
            if (!_clipTitles.SaveFavorite(fileName, parameters.Favorite))
            {
                PushError("The favorite could not be saved — check the recording folder is writable.");
                return;
            }
        }
        else
        {
            if (!_metadata.SaveFavorite(fileName, relative, parameters.Favorite))
            {
                PushError("The favorite could not be saved — check the recording folder is writable.");
                return;
            }
        }

        PushContent();
    }

    private string? ResolveContentFile(string fileName)
    {
        var candidate = _content.ResolveWithinRoot(fileName);
        if (candidate is null || !File.Exists(candidate))
            return null;
        return candidate;
    }
}
