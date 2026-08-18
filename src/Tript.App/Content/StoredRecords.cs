// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;

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
    // The shared atomic write, kept as a named seam because the record stores read as a unit.
    internal static void WriteAtomically(string path, string contents) =>
        AtomicFile.WriteAllText(path, contents);
}
