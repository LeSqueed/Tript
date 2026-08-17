// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.App.Content;

// What a load of a small JSON record in the metadata/ tree found. The distinction exists because
// "there is no record" and "there is a record and it could not be read" are the same thing to a
// reader and opposite things to a writer:
//
//   Absent      Nothing on disk. A writer may create the record; a reader shows the item without a
//               title, a game or bookmarks, which is a normal state for content copied in by hand.
//   Loaded      Read and parsed. A writer read-modify-writes it.
//   Unreadable  There is a file and its contents could not be turned into a record. A writer must
//               leave it alone: the record may hold a game, a user title and a bookmark list, none
//               of which can be recomputed, while everything a writer wants to store here (a probed
//               duration above all) can be.
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
    // over the target.
    //
    // Measured, on the plain File.WriteAllText this replaced: with one thread rewriting a record and
    // another reading it exactly the way the stores read it, 115041 of 506391 reads (22.7%) threw
    // JsonException — most often "The input does not contain any JSON tokens", the window in which
    // WriteAllText has truncated the file and not yet written the new bytes. The host can be in that
    // situation for real, and its own comments say so: a finished clip pushes content from its own
    // thread while the IPC thread may be listing, and a list both reads and writes these records. A
    // reader that hits the window sees an unreadable record, which used to mean a blank record was
    // written over a good one.
    //
    // A rename is atomic on both targets (rename(2) on Linux, MoveFileEx with REPLACE_EXISTING on
    // Windows), so a concurrent reader sees either the whole old record or the whole new one. On
    // Windows the replace can instead fail outright while another handle has the file open, which is
    // the failure the callers already handle: a write that returns false and is retried on the next
    // push, rather than a record that is destroyed.
    internal static void WriteAtomically(string path, string contents)
    {
        // A rename does not consult the destination's own permissions — rename(2) needs a writable
        // directory and nothing else, and MoveFileEx replaces a read-only file just as happily — so
        // the attribute has to be checked here. Without this, moving to an atomic write would quietly
        // start overwriting records that are marked read-only exactly to stop that, where the
        // File.WriteAllText it replaced failed with UnauthorizedAccessException. On Unix the
        // ReadOnly attribute is the file's missing write bits, so this covers a record on read-only
        // media as well as one the user protected on purpose.
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
