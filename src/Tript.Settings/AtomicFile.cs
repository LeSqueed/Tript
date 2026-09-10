// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Settings;

public static class AtomicFile
{
    private const int RenameRetryAttempts = 50;
    private static readonly TimeSpan RenameRetryDelay = TimeSpan.FromMilliseconds(20);
    internal static readonly TimeSpan RenameRetryWindow =
        TimeSpan.FromMilliseconds(RenameRetryAttempts * RenameRetryDelay.TotalMilliseconds);

    public static void WriteAllText(string path, string contents)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
            throw new UnauthorizedAccessException($"'{path}' is read-only and was not rewritten.");

        var temporary = $"{path}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp";
        File.WriteAllText(temporary, contents);
        try
        {
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
