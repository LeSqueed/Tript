// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.Settings;

namespace Tript.App.Content;

// The on-disk metadata store. Every recording's metadata — game, start
// time, content type, audio track layout and its bookmarks — lives in a single metadata/ tree under
// the recording root, keyed by the video file's name.
internal sealed class RecordingMetadataStore
{
    private string _metadataRoot;

    internal RecordingMetadataStore(string metadataRoot)
    {
        _metadataRoot = metadataRoot;
    }

    // Switches the metadata tree to a new root (a settings change that moves the recording output
    // directory moves the metadata tree with it).
    internal void UpdateRoot(string metadataRoot)
    {
        _metadataRoot = metadataRoot;
    }

    // The record for a video, or null when the video has no usable metadata record. A video with no
    // record still lists — empty bookmarks, no title — so "no record" is a normal state, not an
    // error, and a malformed record must not take the video's list entry down with it either: the
    // read path deliberately cannot tell the two apart.
    internal RecordingMetadata? Load(string videoFileName) => Read(videoFileName).Record;

    // The record together with what the load actually found, for the callers that write back. The
    // three states are genuinely different outcomes and only this method reports them.
    internal StoredRecord<RecordingMetadata> Read(string videoFileName)
    {
        var path = PathFor(videoFileName);
        if (!File.Exists(path))
            return StoredRecord<RecordingMetadata>.Absent;

        try
        {
            var record = JsonSerializer.Deserialize<RecordingMetadata>(File.ReadAllText(path),
                SettingsSerialization.Options);
            // A file holding the literal "null" parses to no record at all. There is nothing to
            // preserve in it, but there is a file, so it counts as present-and-unreadable rather
            // than absent — the write path may replace it, the same as any other unusable record,
            // only after saying so.
            return record is null
                ? StoredRecord<RecordingMetadata>.Unreadable
                : new StoredRecord<RecordingMetadata>(StoredRecordState.Loaded, record);
        }
        catch (JsonException exception)
        {
            return new StoredRecord<RecordingMetadata>(StoredRecordState.Unreadable, null,
                exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The record exists and this process cannot read it (a lock, a permission, a failing
            // disk). Least of all may it be overwritten now: the state is "unreadable", not "gone".
            return new StoredRecord<RecordingMetadata>(StoredRecordState.Unreadable, null,
                exception.Message);
        }
    }

    // Persists the metadata record. Returns true when the record was written, false when the write
    // failed (read-only media, disk full, permissions).
    internal bool Save(RecordingMetadata metadata)
    {
        var videoFileName = metadata.VideoFileName();
        if (string.IsNullOrWhiteSpace(videoFileName))
        {
            // The record's own link key is its file name. A record with no VideoPath would be
            // written as "<metadataRoot>/.metadata.json", a file no video can ever be matched to,
            // so it is refused rather than left as litter in the metadata tree.
            Console.Error.WriteLine(
                "Tript.App: refusing to write a metadata record with no videoPath — it would not belong to any video.");
            return false;
        }

        try
        {
            Directory.CreateDirectory(_metadataRoot);
            RecordFile.WriteAtomically(PathFor(videoFileName),
                JsonSerializer.Serialize(metadata, SettingsSerialization.Options));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Tript.App: could not write metadata record: {exception.Message}");
            return false;
        }
    }

    // Removes the record for a video, when there is one. Deleting a video removes its record too
    // (the cascade-delete contract): the metadata/ tree never keeps an orphaned record for a video
    // that is gone.
    internal bool Delete(string videoFileName)
    {
        try
        {
            File.Delete(PathFor(videoFileName));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Tript.App: could not delete metadata record: {exception.Message}");
            return false;
        }
    }

    // <metadataRoot>/<videoFileName>.metadata.json — keyed by the video's file name so a record
    // is addressable without parsing anything.
    internal string PathFor(string videoFileName) =>
        Path.Combine(_metadataRoot, $"{videoFileName}.metadata.json");
}

internal static class RecordingMetadataExtensions
{
    internal static string VideoFileName(this RecordingMetadata metadata)
    {
        // The VideoPath is the '/' separated path relative to the recording root; its file name
        // is the record's key.
        var separator = metadata.VideoPath.LastIndexOf('/');
        return separator >= 0 ? metadata.VideoPath[(separator + 1)..] : metadata.VideoPath;
    }
}
