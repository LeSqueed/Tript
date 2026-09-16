// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.Settings;
using Serilog;

namespace Tript.App.Content;

internal sealed class ClipTitleStore
{
    private volatile string _metadataRoot;
    private readonly object _writeGate = new();
    internal object WriteGate => _writeGate;

    internal ClipTitleStore(string metadataRoot)
    {
        _metadataRoot = metadataRoot;
    }

    internal void UpdateRoot(string metadataRoot)
    {
        _metadataRoot = metadataRoot;
    }

    internal string? Load(string clipFileName) => LoadRecord(clipFileName)?.Title;

    internal ClipTitleRecord? LoadRecord(string clipFileName) => Read(clipFileName).Record;

    internal StoredRecord<ClipTitleRecord> Read(string clipFileName)
    {
        return ReadPath(PathFor(clipFileName));
    }

    internal IReadOnlyList<(string ClipFileName, ClipTitleRecord Record)> EnumerateRecords()
    {
        const string suffix = ".title.json";
        string[] paths;
        try
        {
            paths = Directory.GetFiles(_metadataRoot, $"*{suffix}", SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var records = new List<(string ClipFileName, ClipTitleRecord Record)>();
        foreach (var path in paths)
        {
            var recordFileName = Path.GetFileName(path);
            var clipFileName = recordFileName[..^suffix.Length];
            if (clipFileName.Length == 0)
                continue;

            var record = ReadPath(path).Record;
            if (record is not null)
                records.Add((clipFileName, record));
        }

        return records;
    }

    private static StoredRecord<ClipTitleRecord> ReadPath(string path)
    {
        if (!File.Exists(path))
            return StoredRecord<ClipTitleRecord>.Absent;

        try
        {
            string json;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                json = reader.ReadToEnd();
            }

            var record = JsonSerializer.Deserialize<ClipTitleRecord>(json, SettingsSerialization.Options);
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
            return new StoredRecord<ClipTitleRecord>(StoredRecordState.Unreadable, null,
                exception.Message);
        }
    }

    internal bool Save(string clipFileName, string title)
    {
        lock (_writeGate)
        {
            var existing = Read(clipFileName);
            if (existing.MustNotBeOverwritten)
            {
                Log.Warning("{FileName} has a clip record that could not be read ({Failure}); its title is not written, so the record is left as it is.", clipFileName, existing.Failure);
                return false;
            }

            var record = existing.Record ?? new ClipTitleRecord();
            record.Title = title;
            return Write(clipFileName, record);
        }
    }

    internal bool SaveFavorite(string clipFileName, bool favorite)
    {
        lock (_writeGate)
        {
            var existing = Read(clipFileName);
            if (existing.MustNotBeOverwritten)
                return false;

            var record = existing.Record ?? new ClipTitleRecord();
            record.Favorite = favorite;
            return Write(clipFileName, record);
        }
    }

    internal bool SaveDuration(string clipFileName, double durationSeconds)
    {
        lock (_writeGate)
        {
            var existing = Read(clipFileName);
            if (existing.MustNotBeOverwritten)
            {
                Log.Warning("{FileName} has a clip record that could not be read ({Failure}); the duration is not persisted, so the record is left as it is.", clipFileName, existing.Failure);
                return false;
            }

            var record = existing.Record ?? new ClipTitleRecord();
            record.DurationSeconds = durationSeconds;
            return Write(clipFileName, record);
        }
    }

    internal bool SaveHdrStatus(string clipFileName, bool isHdr)
    {
        lock (_writeGate)
        {
            var existing = Read(clipFileName);
            if (existing.MustNotBeOverwritten)
                return false;
            var record = existing.Record ?? new ClipTitleRecord();
            record.IsHdr = isHdr;
            return Write(clipFileName, record);
        }
    }

    internal bool SaveGame(string clipFileName, string? game, string? gameId)
    {
        lock (_writeGate)
        {
            var existing = Read(clipFileName);
            if (existing.MustNotBeOverwritten)
                return false;

            var record = existing.Record ?? new ClipTitleRecord();
            record.Game = string.IsNullOrWhiteSpace(game) ? null : game;
            record.GameId = string.IsNullOrWhiteSpace(gameId) ? null : gameId;
            return Write(clipFileName, record);
        }
    }

    internal bool SaveConvertedFrom(string sourceFileName, string outputFileName)
    {
        lock (_writeGate)
        {
            var output = Read(outputFileName);
            if (output.MustNotBeOverwritten)
                return false;
            var source = Read(sourceFileName).Record;
            var record = source is null ? new ClipTitleRecord() : new ClipTitleRecord
            {
                Title = source.Title,
                Favorite = source.Favorite,
                DurationSeconds = source.DurationSeconds,
                IsAutomatic = source.IsAutomatic,
                SourceSessionPath = source.SourceSessionPath,
                SourceSessionHighlightsOnly = source.SourceSessionHighlightsOnly,
                ClipStartTime = source.ClipStartTime,
                ClipEndTime = source.ClipEndTime,
                SourceSpans = source.SourceSpans,
                Game = source.Game,
                GameId = source.GameId,
            };
            record.IsHdr = false;
            return Write(outputFileName, record);
        }
    }

    internal bool SaveAutomatic(string clipFileName, string sourceSessionPath,
        double startSeconds, double endSeconds, bool sourceSessionHighlightsOnly = false)
    {
        lock (_writeGate)
        {
            var existing = Read(clipFileName);
            if (existing.MustNotBeOverwritten)
                return false;

            var record = existing.Record ?? new ClipTitleRecord();
            record.IsAutomatic = true;
            record.SourceSessionPath = sourceSessionPath;
            record.SourceSessionHighlightsOnly = sourceSessionHighlightsOnly ? true : null;
            record.ClipStartTime = startSeconds;
            record.ClipEndTime = endSeconds;
            return Write(clipFileName, record);
        }
    }

    internal bool SaveSourceSession(string clipFileName, string sourceSessionPath,
        IReadOnlyList<ClipSourceSpan>? sourceSpans = null)
    {
        lock (_writeGate)
        {
            var existing = Read(clipFileName);
            if (existing.MustNotBeOverwritten)
                return false;

            var record = existing.Record ?? new ClipTitleRecord();
            record.SourceSessionPath = sourceSessionPath;
            if (sourceSpans is { Count: > 0 })
                record.SourceSpans = [.. sourceSpans];
            return Write(clipFileName, record);
        }
    }

    internal bool SaveAutomaticAssignment(string clipFileName, string sourceSessionPath,
        string? game, string? gameId)
    {
        lock (_writeGate)
        {
            var existing = Read(clipFileName);
            if (existing.MustNotBeOverwritten || existing.Record?.IsAutomatic != true)
                return false;

            var record = existing.Record;
            record.SourceSessionPath = sourceSessionPath;
            record.Game = string.IsNullOrWhiteSpace(game) ? null : game;
            record.GameId = string.IsNullOrWhiteSpace(gameId) ? null : gameId;
            return Write(clipFileName, record);
        }
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
            Log.Warning("could not write clip record: {Reason}", exception.Message);
            return false;
        }
    }

    internal bool Delete(string clipFileName)
    {
        lock (_writeGate)
        {
            try
            {
                File.Delete(PathFor(clipFileName));
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log.Warning("could not delete clip record: {Reason}", exception.Message);
                return false;
            }
        }
    }

    internal string PathFor(string clipFileName) =>
        Path.Combine(_metadataRoot, $"{clipFileName}.title.json");
}

internal sealed class ClipTitleRecord
{
    public string Title { get; set; } = string.Empty;

    public bool Favorite { get; set; }

    public double? DurationSeconds { get; set; }

    public bool? IsHdr { get; set; }

    public bool IsAutomatic { get; set; }

    public string? SourceSessionPath { get; set; }

    public bool? SourceSessionHighlightsOnly { get; set; }

    public string? Game { get; set; }

    public string? GameId { get; set; }

    public double? ClipStartTime { get; set; }

    public double? ClipEndTime { get; set; }

    public List<ClipSourceSpan>? SourceSpans { get; set; }
}

internal sealed class ClipSourceSpan
{
    public double Start { get; set; }

    public double End { get; set; }
}
