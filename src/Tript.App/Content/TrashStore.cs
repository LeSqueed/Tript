// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.Settings;

namespace Tript.App.Content;

// The recycle bin under the recording root. Deleting content moves the video and every record keyed
// to it into <root>/.trash/<entryId>/files/<original path relative to the root>, so a restore is a
// move back to the path the mirror already spells out and a purge is one recursive directory
// delete.
internal sealed class TrashStore
{
    internal const string DirectoryName = ".trash";

    private const string FilesDirectoryName = "files";
    private const string RecordFileName = "entry.json";

    private readonly object _rootGate = new();
    private string _trashRoot;

    internal TrashStore(string trashRoot)
    {
        _trashRoot = Path.GetFullPath(trashRoot);
    }

    // Switches the bin to a new root (a settings change that moves the recording output directory
    // moves the trash with it).
    internal void UpdateRoot(string trashRoot)
    {
        lock (_rootGate)
            _trashRoot = Path.GetFullPath(trashRoot);
    }

    internal string Root
    {
        get
        {
            lock (_rootGate)
                return _trashRoot;
        }
    }

    // ---- delete ----

    // Moves one item's files into a fresh entry and returns it, or null with the reason in
    // `failure`. `seed` carries the fields only the caller can know (the content type and the
    // display fields read off the records before they moved).
    internal TrashEntryRecord? Add(IReadOnlyList<TrashedFile> files, TrashEntryRecord seed, out string? failure)
    {
        failure = null;
        if (files.Count == 0)
        {
            failure = "there was nothing left to move";
            return null;
        }

        var id = NewId();
        var entryDirectory = Path.Combine(Root, id);
        var filesRoot = Path.Combine(entryDirectory, FilesDirectoryName);
        var moved = new List<(string Source, string Destination)>();

        try
        {
            Directory.CreateDirectory(filesRoot);
            foreach (var file in files)
            {
                var destination = Path.Combine(filesRoot, ToPlatformPath(file.RelativePath));
                MoveFile(file.SourcePath, destination);
                moved.Add((file.SourcePath, destination));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var rollbackFailures = new List<string>();
            for (var index = moved.Count - 1; index >= 0; index--)
            {
                try
                {
                    MoveFile(moved[index].Destination, moved[index].Source);
                }
                catch (Exception rollbackException) when (rollbackException is IOException or UnauthorizedAccessException)
                {
                    rollbackFailures.Add(rollbackException.Message);
                }
            }

            failure = rollbackFailures.Count == 0
                ? exception.Message
                : $"{exception.Message}; rollback also failed: {string.Join("; ", rollbackFailures)}";
            if (rollbackFailures.Count == 0)
                DeleteDirectory(entryDirectory);
            return null;
        }

        seed.Id = id;
        // Written last: until it exists the mirror under files/ is already the whole truth, and a
        // crash between the move and this write costs a title, not a video.
        try
        {
            RecordFile.WriteAtomically(Path.Combine(entryDirectory, RecordFileName),
                JsonSerializer.Serialize(seed, SettingsSerialization.Options));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"Tript.App: the trash entry '{id}' was moved but its record could not be written " +
                $"({exception.Message}); it is listed from the files themselves instead.");
        }

        return seed;
    }

    // ---- list ----

    // Every entry in the bin, newest first. The relative path is the tiebreak so the order is total.
    internal List<TrashEntryRecord> List()
    {
        var entries = new List<TrashEntryRecord>();
        var root = Root;
        if (!Directory.Exists(root))
            return entries;

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Tript.App: the trash could not be listed: {exception.Message}");
            return entries;
        }

        foreach (var directory in directories)
        {
            var entry = ReadRecord(directory) ?? Reconstruct(directory);
            if (entry is not null)
                entries.Add(entry);
        }

        entries.Sort((left, right) =>
        {
            var byDate = right.DeletedAt.CompareTo(left.DeletedAt);
            return byDate != 0 ? byDate : string.CompareOrdinal(left.Id, right.Id);
        });

        return entries;
    }

    // ---- restore ----

    internal TrashRestoreResult Restore(string entryId, string effectiveRoot)
    {
        var entryDirectory = ResolveEntry(entryId);
        if (entryDirectory is null || !Directory.Exists(entryDirectory))
            return TrashRestoreResult.Failed(entryId, "there is no such entry in the trash");

        var filesRoot = Path.Combine(entryDirectory, FilesDirectoryName);
        List<string> stored;
        try
        {
            stored = Directory.Exists(filesRoot)
                ? Directory.EnumerateFiles(filesRoot, "*", SearchOption.AllDirectories).ToList()
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return TrashRestoreResult.Failed(entryId, exception.Message);
        }

        if (stored.Count == 0)
            return TrashRestoreResult.Failed(entryId, "the entry holds no files to put back");

        // Where everything goes comes from the mirror under files/, never from the record: a
        // hand-edited record must not be able to aim a restore at a path of its choosing.
        var video = stored.FirstOrDefault(file =>
            Path.GetExtension(file).Equals(".mp4", StringComparison.OrdinalIgnoreCase)) ?? stored[0];
        var originalName = Path.GetFileName(video);
        var originalDirectory = Path.GetDirectoryName(Path.GetRelativePath(filesRoot, video)) ?? string.Empty;

        // The video's own name may be taken again — a re-record under the same name, or an earlier
        // restore of a second copy — so a free name is chosen here and every record keyed to the old
        // one follows it. Nothing is ever moved over a file that is already there.
        var restoredName = FreeFileName(Path.Combine(effectiveRoot, originalDirectory), originalName);
        if (restoredName is null)
            return TrashRestoreResult.Failed(entryId, "no free name could be found for the restored file");

        var kept = 0;
        var moved = new List<(string Source, string Destination)>();
        try
        {
            foreach (var file in stored)
            {
                var relative = RekeyLeaf(Path.GetRelativePath(filesRoot, file), originalName, restoredName);
                var target = ContentServer.ResolveWithinRoot(effectiveRoot, ToWirePath(relative));
                if (target is null)
                {
                    Console.Error.WriteLine(
                        $"Tript.App: '{relative}' does not resolve inside the recording folder, so it stays in the trash.");
                    kept++;
                    continue;
                }

                if (File.Exists(target))
                {
                    // Only the video was re-keyed; a stale record left behind under the same key is
                    // the one thing that can still collide, and its owner on disk wins.
                    Console.Error.WriteLine(
                        $"Tript.App: '{target}' already exists, so the trashed copy was left in the trash.");
                    kept++;
                    continue;
                }

                MoveFile(file, target);
                moved.Add((file, target));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var rollbackFailures = new List<string>();
            for (var index = moved.Count - 1; index >= 0; index--)
            {
                try
                {
                    MoveFile(moved[index].Destination, moved[index].Source);
                }
                catch (Exception rollbackException) when (rollbackException is IOException or UnauthorizedAccessException)
                {
                    rollbackFailures.Add(rollbackException.Message);
                }
            }

            var failure = rollbackFailures.Count == 0
                ? exception.Message
                : $"{exception.Message}; rollback also failed: {string.Join("; ", rollbackFailures)}";
            if (rollbackFailures.Count == 0)
                DeleteDirectory(entryDirectory);
            return TrashRestoreResult.Failed(entryId, failure);
        }

        // Only when the entry is genuinely empty of anything worth keeping. Deleting it whenever the
        // loop finished would destroy exactly the files the skips above just said were being kept —
        // a recording's title and bookmarks, for the sake of tidying up an empty directory.
        if (kept == 0)
            DeleteDirectory(entryDirectory);

        var renamed = !string.Equals(restoredName, originalName, StringComparison.Ordinal);
        return new TrashRestoreResult(entryId, originalName, restoredName, renamed, null, kept);
    }

    // ---- purge ----

    internal bool Purge(string entryId, out string? failure)
    {
        failure = null;
        var entryDirectory = ResolveEntry(entryId);
        if (entryDirectory is null)
        {
            failure = "there is no such entry in the trash";
            return false;
        }

        if (!Directory.Exists(entryDirectory))
            return true;

        try
        {
            Directory.Delete(entryDirectory, recursive: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failure = exception.Message;
            return false;
        }
    }

    // ---- the records ----

    private static TrashEntryRecord? ReadRecord(string entryDirectory)
    {
        var path = Path.Combine(entryDirectory, RecordFileName);
        if (!File.Exists(path))
            return null;

        try
        {
            var record = JsonSerializer.Deserialize<TrashEntryRecord>(File.ReadAllText(path),
                SettingsSerialization.Options);
            if (record is null || string.IsNullOrEmpty(record.FileName))
                return null;

            // The directory name is the id, always: a hand-edited record cannot re-address itself.
            record.Id = Path.GetFileName(entryDirectory);
            return record;
        }
        catch (Exception exception) when (exception is JsonException or IOException
                                             or UnauthorizedAccessException)
        {
            // Never rewritten — the files it describes are still under files/, and the listing is
            // rebuilt from those instead.
            Console.Error.WriteLine(
                $"Tript.App: the trash record for '{Path.GetFileName(entryDirectory)}' could not be read " +
                $"({exception.Message}); the entry is listed from its files instead.");
            return null;
        }
    }

    // The entry as the mirror under files/ describes it, for an entry whose record is missing or
    // unreadable. The video's original path is the mirror's own relative path, which is the only
    // fact a restore actually needs.
    private static TrashEntryRecord? Reconstruct(string entryDirectory)
    {
        var filesRoot = Path.Combine(entryDirectory, FilesDirectoryName);
        if (!Directory.Exists(filesRoot))
            return null;

        List<string> stored;
        try
        {
            stored = Directory.EnumerateFiles(filesRoot, "*", SearchOption.AllDirectories).ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (stored.Count == 0)
            return null;

        var video = stored.FirstOrDefault(file =>
            Path.GetExtension(file).Equals(".mp4", StringComparison.OrdinalIgnoreCase)) ?? stored[0];
        var relative = ToWirePath(Path.GetRelativePath(filesRoot, video));

        return new TrashEntryRecord
        {
            Id = Path.GetFileName(entryDirectory),
            ContentType = relative.StartsWith("clips/", StringComparison.Ordinal) ? "clip" : "recording",
            FileName = Path.GetFileName(video),
            OriginalPath = relative,
            FileSizeBytes = SafeLength(video),
            DeletedAt = new DateTimeOffset(Directory.GetLastWriteTimeUtc(entryDirectory)).ToUnixTimeSeconds(),
        };
    }

    // ---- paths ----

    // The absolute directory for an entry id, or null when the id is not one this bin can address.
    // The id is a single directory name, so a traversal cannot ride in on it, and the resolution
    // still goes through the content server's guard against the trash root.
    private string? ResolveEntry(string entryId)
    {
        if (string.IsNullOrWhiteSpace(entryId) || entryId.Length > 128)
            return null;
        foreach (var character in entryId)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
                return null;
        }

        var resolved = ContentServer.ResolveWithinRoot(Root, entryId, allowTrash: true);
        return resolved is null || string.Equals(resolved, Root, StringComparison.Ordinal) ? null : resolved;
    }

    // A file name inside the item's original directory that nothing occupies yet, or the original
    // name when it is free. Null when even the suffixed names are all taken.
    private static string? FreeFileName(string directory, string fileName)
    {
        if (!File.Exists(Path.Combine(directory, fileName)))
            return fileName;

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var attempt = 1; attempt <= 999; attempt++)
        {
            var candidate = $"{baseName}-restored-{attempt}{extension}";
            if (!File.Exists(Path.Combine(directory, candidate)))
                return candidate;
        }

        return null;
    }

    // Re-keys a stored relative path onto the restored file name. Every record in the metadata tree
    // is keyed by the video's file name (<name>.metadata.json, <name>.title.json, <name>.jpg), so a
    // video that comes back under a new name takes its records with it.
    private static string RekeyLeaf(string relativePath, string originalName, string restoredName)
    {
        if (string.Equals(originalName, restoredName, StringComparison.Ordinal))
            return relativePath;

        var leaf = Path.GetFileName(relativePath);
        if (!leaf.StartsWith(originalName, StringComparison.Ordinal))
            return relativePath;

        var rekeyed = restoredName + leaf[originalName.Length..];
        var directory = Path.GetDirectoryName(relativePath);
        return string.IsNullOrEmpty(directory) ? rekeyed : Path.Combine(directory, rekeyed);
    }

    private static void MoveFile(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            File.Move(source, destination);
        }
        catch (IOException)
        {
            // A recording root on another volume than its trash cannot be renamed into place; the
            // copy is the slow path, and the source only goes once the copy is on disk.
            File.Copy(source, destination, overwrite: false);
            try
            {
                File.Delete(source);
            }
            catch
            {
                try { File.Delete(destination); } catch { /* preserve the original failure */ }
                throw;
            }
        }
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Tript.App: could not empty the trash entry '{directory}': {exception.Message}");
        }
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string NewId() =>
        $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid().ToString("N")[..8]}";

    internal static string ToWirePath(string path) => path.Replace(Path.DirectorySeparatorChar, '/');

    private static string ToPlatformPath(string path) => path.Replace('/', Path.DirectorySeparatorChar);
}

// One file on its way into or out of the bin: where it is now, and where it belongs relative to the
// recording root.
internal readonly record struct TrashedFile(string SourcePath, string RelativePath);

// What a restore did. `RestoredAs` is the name the file actually came back under, which is the
// original one unless something already occupied it.
internal readonly record struct TrashRestoreResult(
    string EntryId,
    string? FileName,
    string? RestoredAs,
    bool Renamed,
    string? Failure,
    // Files that could not be put back and are still in the trash. Non-zero means the entry was kept.
    int KeptInTrash = 0)
{
    internal static TrashRestoreResult Failed(string entryId, string failure) =>
        new(entryId, null, null, false, failure);
}

// The stored half of a trash entry. purgeAt is deliberately not persisted: it is derived from the
// current retention on every read, so changing the setting re-dates the whole bin instead of
// leaving entries pinned to the retention that happened to be set when they were deleted.
internal sealed class TrashEntryRecord
{
    public string Id { get; set; } = string.Empty;

    public string ContentType { get; set; } = "recording";

    public string FileName { get; set; } = string.Empty;

    // The video's path relative to the recording root, '/' separated — where a restore puts it back.
    public string OriginalPath { get; set; } = string.Empty;

    public string? Title { get; set; }

    public string? Game { get; set; }

    public double? DurationSeconds { get; set; }

    public long? FileSizeBytes { get; set; }

    public long DeletedAt { get; set; }
}
