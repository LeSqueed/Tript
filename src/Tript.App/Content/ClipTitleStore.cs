// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.Settings;

namespace Tript.App.Content;

// The on-disk store for clip titles. A clip's user title ("The clutch") has no RecordingMetadata
// record — the metadata store is for recordings — so it lives in its own tiny record in the same
// metadata/ tree, keyed by the clip's file name. Metadata deliberately never sits next to the
// video: the clips directory stays plain MP4s, and this record is how the library rebuilds the
// list.
//
// A record is path-keyed: <root>/metadata/<clipFileName>.title.json, where clipFileName is the
// .mp4's own file name (for example "session-20260817-083000-clip-k2m3xq.mp4"). The record is just
// { "title": "..." }; an absent or malformed record means the clip falls back to its
// file-name-without-extension as the title, exactly like a recording with no metadata record.
internal sealed class ClipTitleStore
{
    private string _metadataRoot;

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

    // The title for a clip, or null when the clip has no title record yet (missing or malformed).
    // A clip with no record still lists — title falls back to the file name — so "no record" is a
    // normal state, not an error.
    internal string? Load(string clipFileName)
    {
        var path = PathFor(clipFileName);
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ClipTitleRecord>(File.ReadAllText(path),
                SettingsSerialization.Options)?.Title;
        }
        catch (JsonException)
        {
            // A malformed record must not take the clip's list entry down with it; the caller
            // treats a failed load exactly like an absent record.
            return null;
        }
    }

    // Persists a clip's title. Returns true when the record was written, false when the write
    // failed (read-only media, disk full, permissions). The failure is logged here; a clip whose
    // title could not be written still completes and still lists, just under its file name.
    internal bool Save(string clipFileName, string title)
    {
        try
        {
            Directory.CreateDirectory(_metadataRoot);
            File.WriteAllText(PathFor(clipFileName),
                JsonSerializer.Serialize(new ClipTitleRecord { Title = title }, SettingsSerialization.Options));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Tript.App: could not write clip title record: {exception.Message}");
            return false;
        }
    }

    // Removes the title record for a clip, when there is one. Deleting a clip removes its title
    // record too (the cascade-delete contract): the metadata/ tree never keeps an orphaned record
    // for a clip that is gone.
    internal bool Delete(string clipFileName)
    {
        try
        {
            File.Delete(PathFor(clipFileName));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Tript.App: could not delete clip title record: {exception.Message}");
            return false;
        }
    }

    // <metadataRoot>/<clipFileName>.title.json — keyed by the clip's file name so a record is
    // addressable without parsing anything.
    private string PathFor(string clipFileName) =>
        Path.Combine(_metadataRoot, $"{clipFileName}.title.json");
}

internal sealed class ClipTitleRecord
{
    public string Title { get; set; } = string.Empty;
}
