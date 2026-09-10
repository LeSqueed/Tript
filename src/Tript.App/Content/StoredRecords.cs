// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;

namespace Tript.App.Content;

internal enum StoredRecordState
{
    Absent,
    Loaded,
    Unreadable,
}

internal readonly record struct StoredRecord<TRecord>(
    StoredRecordState State,
    TRecord? Record,
    string? Failure = null)
    where TRecord : class
{
    internal static StoredRecord<TRecord> Absent { get; } = new(StoredRecordState.Absent, null);

    internal static StoredRecord<TRecord> Unreadable { get; } =
        new(StoredRecordState.Unreadable, null, "the file holds no record, only a JSON null");

    internal bool MustNotBeOverwritten => State == StoredRecordState.Unreadable;
}

internal static class RecordFile
{
    internal static void WriteAtomically(string path, string contents) =>
        AtomicFile.WriteAllText(path, contents);
}
