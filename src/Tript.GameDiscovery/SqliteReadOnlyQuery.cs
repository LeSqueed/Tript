// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;

namespace Tript.GameDiscovery;

public static partial class SqliteReadOnlyQuery
{
    public const string Library = "libsqlite3.so.0";
    private const int SqliteOk = 0;
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;
    private const int SqliteOpenReadOnly = 0x1;
    private const int BusyTimeoutMilliseconds = 1000;

    public static IReadOnlyList<string?[]> Rows(string databasePath, string sql)
    {
        var status = sqlite3_open_v2(databasePath, out var database, SqliteOpenReadOnly, null);
        try
        {
            if (status != SqliteOk)
                throw Failure(database, status, databasePath);
            _ = sqlite3_busy_timeout(database, BusyTimeoutMilliseconds);
            return ReadRows(database, sql, databasePath);
        }
        finally
        {
            if (database != 0)
                _ = sqlite3_close(database);
        }
    }

    private static List<string?[]> ReadRows(nint database, string sql, string databasePath)
    {
        var status = sqlite3_prepare_v2(database, sql, -1, out var statement, 0);
        try
        {
            if (status != SqliteOk)
                throw Failure(database, status, databasePath);

            var columns = sqlite3_column_count(statement);
            var rows = new List<string?[]>();
            while ((status = sqlite3_step(statement)) == SqliteRow)
            {
                var row = new string?[columns];
                for (var column = 0; column < columns; column++)
                    row[column] = Marshal.PtrToStringUTF8(sqlite3_column_text(statement, column));
                rows.Add(row);
            }
            if (status != SqliteDone)
                throw Failure(database, status, databasePath);
            return rows;
        }
        finally
        {
            if (statement != 0)
                _ = sqlite3_finalize(statement);
        }
    }

    private static IOException Failure(nint database, int status, string databasePath)
    {
        var message = database != 0 ? Marshal.PtrToStringUTF8(sqlite3_errmsg(database)) : null;
        return new IOException($"SQLite error {status} reading {databasePath}: {message ?? "unknown error"}");
    }

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int sqlite3_open_v2(string filename, out nint database, int flags, string? vfs);

    [LibraryImport(Library)]
    private static partial int sqlite3_busy_timeout(nint database, int milliseconds);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int sqlite3_prepare_v2(nint database, string sql, int length, out nint statement, nint tail);

    [LibraryImport(Library)]
    private static partial int sqlite3_step(nint statement);

    [LibraryImport(Library)]
    private static partial int sqlite3_column_count(nint statement);

    [LibraryImport(Library)]
    private static partial nint sqlite3_column_text(nint statement, int column);

    [LibraryImport(Library)]
    private static partial nint sqlite3_errmsg(nint database);

    [LibraryImport(Library)]
    private static partial int sqlite3_finalize(nint statement);

    [LibraryImport(Library)]
    private static partial int sqlite3_close(nint database);
}
