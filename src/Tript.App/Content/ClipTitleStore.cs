// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.Settings;

namespace Tript.App.Content;

// The on-disk store for a clip's own record. A clip has no RecordingMetadata record — the metadata
// store is for recordings — so the few facts the library needs about a clip live in their own tiny
// record in the same metadata/ tree, keyed by the clip's file name.
internal sealed class ClipTitleStore
{
    // See ContentServer._contentRoot: written on the IPC thread, read from the library and clip
    // threads.
    private volatile string _metadataRoot;

    internal ClipTitleStore(string metadataRoot)
    {
        _metadataRoot = metadataRoot;
    }

    // Switches the metadata tree to a new root (a settings change that moves the recording output
    // directory moves the metadata tree with it).
    internal void UpdateRoot(string metadataRoot)
    {
        _metadataRoot = metadataRoot;
    }

    // The title for a clip, or null when the clip has no record yet (missing or malformed).
    // A clip with no record still lists — title falls back to the file name — so "no record" is a
    // normal state, not an error.
    internal string? Load(string clipFileName) => LoadRecord(clipFileName)?.Title;

    // The whole record, for the caller that needs more than the title (the library reads the
    // duration from it in the same pass). Null for a record that is absent and for one that could
    // not be read: the read path cannot tell them apart, on purpose, because a clip lists either
    // way. Every caller that writes back must use Read instead.
    internal ClipTitleRecord? LoadRecord(string clipFileName) => Read(clipFileName).Record;

    // The record together with what the load found, for the callers that write back. See
    // StoredRecordState: overwriting an unreadable record here costs the user's clip title.
    internal StoredRecord<ClipTitleRecord> Read(string clipFileName)
    {
        var path = PathFor(clipFileName);
        if (!File.Exists(path))
            return StoredRecord<ClipTitleRecord>.Absent;

        try
        {
            var record = JsonSerializer.Deserialize<ClipTitleRecord>(File.ReadAllText(path),
                SettingsSerialization.Options);
            return record is null
                ? StoredRecord<ClipTitleRecord>.Unreadable
                : new StoredRecord<ClipTitleRecord>(StoredRecordState.Loaded, record);
        }
        catch (JsonException exception)
        {
            return new StoredRecord<ClipTitleRecord>(StoredRecordState.Unreadable, null,
                exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The record exists and cannot be read right now (a lock, a permission, a failing
            // disk). "Unreadable" is not "gone", so nothing may be written over it.
            return new StoredRecord<ClipTitleRecord>(StoredRecordState.Unreadable, null,
                exception.Message);
        }
    }

    // Persists a clip's title. Returns true when the record was written, false when the write
    // failed (read-only media, disk full, permissions, or an existing record that could not be
    // read).
    internal bool Save(string clipFileName, string title)
    {
        // Read-modify-write, not overwrite: the record holds more than the title now, and a rename
        // must not drop a duration that was already discovered (or the other way round).
        var existing = Read(clipFileName);
        if (existing.MustNotBeOverwritten)
        {
            Console.Error.WriteLine(
                $"Tript.App: '{clipFileName}' has a clip record that could not be read " +
                $"({existing.Failure}); its title is not written, so the record is left as it is.");
            return false;
        }

        var record = existing.Record ?? new ClipTitleRecord();
        record.Title = title;
        return Write(clipFileName, record);
    }

    // Persists a clip's duration, leaving any title it already has alone.
    internal bool SaveDuration(string clipFileName, double durationSeconds)
    {
        var existing = Read(clipFileName);
        if (existing.MustNotBeOverwritten)
        {
            // The duration is the cheap half of this record and the title is the irreplaceable
            // half: re-probing costs one ffprobe, a lost title cannot be recovered at all. So the
            // unreadable record stands and the duration is simply not persisted this time.
            Console.Error.WriteLine(
                $"Tript.App: '{clipFileName}' has a clip record that could not be read " +
                $"({existing.Failure}); the duration is not persisted, so the record is left as it is.");
            return false;
        }

        var record = existing.Record ?? new ClipTitleRecord();
        record.DurationSeconds = durationSeconds;
        return Write(clipFileName, record);
    }

    private bool Write(string clipFileName, ClipTitleRecord record)
    {
        try
        {
            Directory.CreateDirectory(_metadataRoot);
            RecordFile.WriteAtomically(PathFor(clipFileName),
                JsonSerializer.Serialize(record, SettingsSerialization.Options));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Tript.App: could not write clip record: {exception.Message}");
            return false;
        }
    }

    // Removes the record for a clip, when there is one. Deleting a clip removes its record too (the
    // cascade-delete contract): the metadata/ tree never keeps an orphaned record for a clip that is
    // gone.
    internal bool Delete(string clipFileName)
    {
        try
        {
            File.Delete(PathFor(clipFileName));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Tript.App: could not delete clip record: {exception.Message}");
            return false;
        }
    }

    // <metadataRoot>/<clipFileName>.title.json — keyed by the clip's file name so a record is
    // addressable without parsing anything.
    internal string PathFor(string clipFileName) =>
        Path.Combine(_metadataRoot, $"{clipFileName}.title.json");
}

internal sealed class ClipTitleRecord
{
    public string Title { get; set; } = string.Empty;

    // The clip's playing length in seconds, as the container reports it, or null when it has not
    // been read yet. Null is serialized away (SettingsSerialization ignores nulls), so a record
    // written before this field existed still loads and still round-trips.
    public double? DurationSeconds { get; set; }
}
