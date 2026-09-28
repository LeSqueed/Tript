// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Serilog;
using Tript.App.Content;

namespace Tript.App;

internal static class RecordingRelocation
{
    internal static bool TryMoveAll(IReadOnlyList<(string Source, string Destination)> moves,
        Action<string, string> move, IReadOnlyList<TimeSpan>? retryDelays, out Exception? failure)
    {
        var completed = new List<(string Source, string Destination)>();
        try
        {
            foreach (var (source, destination) in moves)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (retryDelays is null)
                    SharingViolationRetry.Run(() => move(source, destination));
                else
                    SharingViolationRetry.Run(() => move(source, destination), retryDelays);
                completed.Add((source, destination));
            }

            failure = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RollBack(completed, move);
            failure = exception;
            return false;
        }
    }

    internal static void RollBack(IReadOnlyList<(string Source, string Destination)> completed,
        Action<string, string> move)
    {
        for (var index = completed.Count - 1; index >= 0; index--)
        {
            var (source, destination) = completed[index];
            try
            {
                if (File.Exists(destination) && !File.Exists(source))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(source)!);
                    move(destination, source);
                }
            }
            catch (Exception rollbackException) when (rollbackException is IOException or UnauthorizedAccessException)
            {
                Log.Error(rollbackException, "AppHost: recording reassignment rollback failed for {Path}", source);
            }
        }
    }
}
