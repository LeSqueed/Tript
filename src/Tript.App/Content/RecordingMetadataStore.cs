// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.Settings;

namespace Tript.App.Content;

// The on-disk metadata store (spec/config-and-storage.md). Every recording's metadata — game,
// start time, content type, audio track layout and its bookmarks — lives in a single metadata/
// tree under the recording root, keyed by the video file's name. Metadata deliberately never sits
// next to the video: the recordings directory stays plain MP4s, and the record is how the library
// rebuilds the list.
//
// A record is path-keyed: <root>/metadata/<videoFileName>.metadata.json, where videoFileName is
// the .mp4's own file name (for example "session-20260817-083000.mp4"). The record itself carries
// the video's relative path (RecordingMetadata.VideoPath, "sessions/session-…mp4") as the link key
// back to the file. Auto bookmarks (written when a recording stops) and user bookmarks
// (AddBookmark/DeleteBookmark) and the user title (RenameContent) all live on the one record per
// video.
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

    // The record for a video, or null when the video has no metadata record yet. A video with no
    // record still lists — empty bookmarks, no title — so "no record" is a normal state, not an
    // error.
    internal RecordingMetadata? Load(string videoFileName)
    {
        var path = PathFor(videoFileName);
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<RecordingMetadata>(File.ReadAllText(path),
                SettingsSerialization.Options);
        }
        catch (JsonException)
        {
            // A malformed record must not take the video's list entry down with it; the caller
            // treats a failed load exactly like an absent record.
            return null;
        }
    }

    internal void Save(RecordingMetadata metadata)
    {
        Directory.CreateDirectory(_metadataRoot);
        try
        {
            File.WriteAllText(PathFor(metadata.VideoFileName()),
                JsonSerializer.Serialize(metadata, SettingsSerialization.Options));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Tript.App: could not write metadata record: {exception.Message}");
        }
    }

    // Removes the record for a video, when there is one. Deleting a video removes its record too
    // (the cascade-delete contract): the metadata/ tree never keeps an orphaned record for a
    // video that is gone.
    internal void Delete(string videoFileName)
    {
        try
        {
            File.Delete(PathFor(videoFileName));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Tript.App: could not delete metadata record: {exception.Message}");
        }
    }

    // <metadataRoot>/<videoFileName>.metadata.json — keyed by the video's file name so a record
    // is addressable without parsing anything.
    private string PathFor(string videoFileName) =>
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
