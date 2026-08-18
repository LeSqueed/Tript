// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.App.Content;

// What a load of a small JSON record in the metadata/ tree found. The distinction exists because
// "there is no record" and "there is a record and it could not be read" are the same thing to a
// reader and opposite things to a writer:   Absent      Nothing on disk.
internal enum StoredRecordState
{
    Absent,
    Loaded,
    Unreadable,
}

// A load's outcome: the state, the record when there is one, and why the read failed when it did
// (kept for the log line — the reason a record could not be read is the only clue the user gets).
internal readonly record struct StoredRecord<TRecord>(
    StoredRecordState State,
    TRecord? Record,
    string? Failure = null)
    where TRecord : class
{
    internal static StoredRecord<TRecord> Absent { get; } = new(StoredRecordState.Absent, null);

    // The state for a file that parsed to no record at all — it holds the literal "null". The reason
    // is spelled out because it ends up in the log line the user sees.
    internal static StoredRecord<TRecord> Unreadable { get; } =
        new(StoredRecordState.Unreadable, null, "the file holds no record, only a JSON null");

    // True when a writer must not touch the file. Spelled out here so no caller has to remember
    // which states are safe to overwrite.
    internal bool MustNotBeOverwritten => State == StoredRecordState.Unreadable;
}

internal static class RecordFile
{
    // Replaces a record's contents in one step, by writing a sibling temporary file and renaming it
    // over the target. Measured, on the plain File.WriteAllText this replaced: with one thread
    // rewriting a record and another reading it exactly the way the stores read it, 115041 of
    // 506391 reads (22.7%) threw JsonException — most often "The input does not contain any JSON
    // tokens", the window in which WriteAllText has truncated the file and not yet written the new
    // bytes.
    internal static void WriteAtomically(string path, string contents)
    {
        // A rename does not consult the destination's own permissions — rename(2) needs a writable
        // directory and nothing else, and MoveFileEx replaces a read-only file just as happily — so
        // the attribute has to be checked here. Without this, moving to an atomic write would
        // quietly start overwriting records that are marked read-only exactly to stop that, where
        // the File.WriteAllText it replaced failed with UnauthorizedAccessException.
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
        {
            throw new UnauthorizedAccessException(
                $"The record '{path}' is read-only and was not rewritten.");
        }

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, contents);
        try
        {
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            // The temporary file must not be left behind next to the records; the metadata tree is
            // enumerated by name and a stray .tmp is litter the next load would have to skip.
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }

            throw;
        }
    }
}
