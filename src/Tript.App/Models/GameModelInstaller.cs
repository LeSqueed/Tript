// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.App.Models;

internal static class GameModelInstaller
{
    internal static FileStream? TryAcquireRootLock(string modelsRoot)
    {
        Directory.CreateDirectory(modelsRoot);
        try
        {
            return new FileStream(Path.Combine(modelsRoot, ".delivery.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    internal static async Task<FileStream> AcquireRootLockAsync(string modelsRoot,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var handle = TryAcquireRootLock(modelsRoot);
            if (handle is not null)
                return handle;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static void InstallValidatedDirectory(string gameId, string stagedPath, string modelsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsRoot);

        var target = Path.Combine(modelsRoot, GameModelPaths.ValidateGameId(gameId));
        var backup = target + ".backup-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(modelsRoot);
        var hadPrevious = Directory.Exists(target);

        if (hadPrevious)
            Directory.Move(target, backup);

        try
        {
            Directory.Move(stagedPath, target);
        }
        catch
        {
            if (hadPrevious && Directory.Exists(backup) && !Directory.Exists(target))
                Directory.Move(backup, target);
            throw;
        }

        if (Directory.Exists(backup))
            Directory.Delete(backup, recursive: true);
    }

    internal static void CleanupInterruptedInstalls(string modelsRoot)
    {
        if (!Directory.Exists(modelsRoot))
            return;

        foreach (var directory in Directory.EnumerateDirectories(modelsRoot))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith(".install-", StringComparison.Ordinal) ||
                name.StartsWith(".download-", StringComparison.Ordinal))
            {
                Directory.Delete(directory, recursive: true);
                continue;
            }

            var backupMarker = name.LastIndexOf(".backup-", StringComparison.Ordinal);
            if (backupMarker > 0)
            {
                var target = Path.Combine(modelsRoot, name[..backupMarker]);
                if (Directory.Exists(target))
                    Directory.Delete(directory, recursive: true);
                else
                    Directory.Move(directory, target);
            }
        }
    }
}
