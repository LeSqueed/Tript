// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Settings;

// Replacing a file's contents in one step, by writing a sibling temporary and renaming it over the
// target.
//
// Measured, on the plain File.WriteAllText this replaced: with one thread rewriting a record and
// another reading it, 115041 of 506391 reads (22.7%) threw JsonException — most often "The input
// does not contain any JSON tokens", the window in which WriteAllText has truncated the file and not
// yet written the new bytes. For the settings file the same window loses every setting at once,
// because a blank file reads as "no settings" and loads defaults.
public static class AtomicFile
{
    // Bounded so a reader that never lets go surfaces rather than hanging the write: the product of
    // the two is the ceiling a caller can wait.
    private const int RenameRetryAttempts = 50;
    private static readonly TimeSpan RenameRetryDelay = TimeSpan.FromMilliseconds(20);
    internal static readonly TimeSpan RenameRetryWindow =
        TimeSpan.FromMilliseconds(RenameRetryAttempts * RenameRetryDelay.TotalMilliseconds);

    public static void WriteAllText(string path, string contents)
    {
        // A rename does not consult the destination's own permissions — rename(2) needs a writable
        // directory and nothing else, and MoveFileEx replaces a read-only file just as happily — so
        // the attribute has to be checked here. Without it, moving to an atomic write would quietly
        // start overwriting files marked read-only exactly to stop that.
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
            throw new UnauthorizedAccessException($"'{path}' is read-only and was not rewritten.");

        // Unique per writer: a fixed "<path>.tmp" is shared by every concurrent writer of the same
        // file, so two interleaved write/rename pairs rename one writer's bytes over the other's.
        var temporary = $"{path}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp";
        File.WriteAllText(temporary, contents);
        try
        {
            // A replace-rename needs delete access on the target while it is in place. On Linux the
            // rename succeeds no matter what handles exist; on Windows it is refused for as long as
            // even one reader holds the file open. Which exception surfaces depends on how that
            // reader opened it: a sharing violation arrives as IOException, an access denial as
            // UnauthorizedAccessException, so both have to be caught. Readers are transient, so a
            // bounded retry lands in a gap between their opens instead of surfacing it to the
            // caller. The cost is that a genuinely fatal IOException (a full disk) takes the whole
            // RenameRetryWindow to report.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temporary, path, overwrite: true);
                    break;
                }
                catch (Exception exception)
                    when (exception is IOException or UnauthorizedAccessException
                          && attempt < RenameRetryAttempts)
                {
                    Thread.Sleep(RenameRetryDelay);
                }
            }
        }
        catch
        {
            // The temporary must not be left behind: these directories are enumerated by name and a
            // stray .tmp is litter the next load would have to skip.
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
