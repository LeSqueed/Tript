// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.Settings;
using Serilog;

namespace Tript.App.Content;

internal sealed class RecordingMetadataStore
{
    private volatile string _metadataRoot;
    private readonly object _writeGate = new();
    internal object WriteGate => _writeGate;

    internal RecordingMetadataStore(string metadataRoot)
    {
        _metadataRoot = metadataRoot;
    }

    internal void UpdateRoot(string metadataRoot)
    {
        _metadataRoot = metadataRoot;
    }

    internal RecordingMetadata? Load(string videoFileName) => Read(videoFileName).Record;

    internal IReadOnlyList<string> EnumerateVideoFileNames()
    {
        const string suffix = ".metadata.json";
        string[] paths;
        try
        {
            paths = Directory.GetFiles(_metadataRoot, $"*{suffix}", SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var names = new List<string>();
        foreach (var path in paths)
        {
            var videoFileName = Path.GetFileName(path)[..^suffix.Length];
            if (videoFileName.Length > 0)
                names.Add(videoFileName);
        }

        return names;
    }

    internal StoredRecord<RecordingMetadata> Read(string videoFileName)
    {
        var path = PathFor(videoFileName);
        if (!File.Exists(path))
            return StoredRecord<RecordingMetadata>.Absent;

        try
        {
            string json;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                json = reader.ReadToEnd();
            }

            var record = JsonSerializer.Deserialize<RecordingMetadata>(json,
                SettingsSerialization.Options);

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
            return new StoredRecord<RecordingMetadata>(StoredRecordState.Unreadable, null,
                exception.Message);
        }
    }

    internal bool Save(RecordingMetadata metadata)
    {
        lock (_writeGate)
        {
            var videoFileName = metadata.VideoFileName();
            if (string.IsNullOrWhiteSpace(videoFileName))
            {
                Log.Warning("refusing to write a metadata record with no videoPath — it would not belong to any video.");
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
                Log.Warning("could not write metadata record: {Reason}", exception.Message);
                return false;
            }
        }
    }

    internal bool SaveFavorite(string videoFileName, string videoPath, bool favorite)
    {
        lock (_writeGate)
        {
            var existing = Read(videoFileName);
            if (existing.MustNotBeOverwritten)
                return false;
            var metadata = existing.Record ?? new RecordingMetadata { VideoPath = videoPath };
            metadata.VideoPath = videoPath;
            metadata.Favorite = favorite;
            return Save(metadata);
        }
    }

    internal bool SaveDuration(string videoFileName, string videoPath, double durationSeconds)
    {
        lock (_writeGate)
        {
            var existing = Read(videoFileName);
            if (existing.MustNotBeOverwritten)
                return false;
            var metadata = existing.Record ?? new RecordingMetadata { VideoPath = videoPath };
            metadata.VideoPath = videoPath;
            metadata.DurationSeconds = durationSeconds;
            return Save(metadata);
        }
    }

    internal bool Delete(string videoFileName)
    {
        lock (_writeGate)
        {
            try
            {
                File.Delete(PathFor(videoFileName));
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log.Warning("could not delete metadata record: {Reason}", exception.Message);
                return false;
            }
        }
    }

    internal string PathFor(string videoFileName) =>
        Path.Combine(_metadataRoot, $"{videoFileName}.metadata.json");
}

internal static class RecordingMetadataExtensions
{
    internal static string VideoFileName(this RecordingMetadata metadata)
    {
        var separator = metadata.VideoPath.LastIndexOf('/');
        return separator >= 0 ? metadata.VideoPath[(separator + 1)..] : metadata.VideoPath;
    }
}
