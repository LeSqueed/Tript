// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

#if TRIPT_TRAINING

using Serilog;

namespace Tript.App.Training;

internal static class TrainingWorkspaceMigration
{
    internal static void Migrate(GameCatalog catalog, string trainingRoot, string installedModelsRoot)
    {
        foreach (var entry in catalog.Entries)
        {
            foreach (var legacyId in entry.LegacyGameIds ?? [])
            {
                try
                {
                    MigrateWorkspaceDirectory(Path.Combine(trainingRoot, legacyId),
                        Path.Combine(trainingRoot, entry.GameId));
                    MigrateModelDirectory(Path.Combine(installedModelsRoot, legacyId),
                        Path.Combine(installedModelsRoot, entry.GameId));
                }
                catch (Exception exception) when (TrainingWorkspace.IsTransientFileSystemError(exception))
                {
                    Log.Warning(exception, "Could not migrate training files from {LegacyGameId} to {GameId}",
                        legacyId, entry.GameId);
                }
            }
        }
    }

    private static void MigrateWorkspaceDirectory(string source, string destination)
    {
        if (!Directory.Exists(source)) return;
        if (!Directory.Exists(destination))
        {
            Directory.Move(source, destination);
            return;
        }

        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, path));
            if (File.Exists(target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(path, target);
        }

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        if (!Directory.EnumerateFileSystemEntries(source).Any()) Directory.Delete(source);
    }

    private static void MigrateModelDirectory(string source, string destination)
    {
        if (!Directory.Exists(source)) return;
        if (!Directory.Exists(destination)) Directory.Move(source, destination);
    }
}

#endif
