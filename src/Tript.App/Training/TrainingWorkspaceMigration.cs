// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

#if TRIPT_TRAINING

using Serilog;

namespace Tript.App.Training;

internal static class TrainingWorkspaceMigration
{
    internal static void Migrate(IEnumerable<KeyValuePair<string, string>> legacyGameIds, string trainingRoot,
        string installedModelsRoot)
    {
        foreach (var (legacyId, gameId) in legacyGameIds)
        {
            try
            {
                MigrateWorkspaceDirectory(Path.Combine(trainingRoot, legacyId),
                    Path.Combine(trainingRoot, gameId));
                MigrateModelDirectory(Path.Combine(installedModelsRoot, legacyId),
                    Path.Combine(installedModelsRoot, gameId));
            }
            catch (Exception exception) when (TrainingWorkspace.IsTransientFileSystemError(exception))
            {
                Log.Warning(exception, "Could not migrate training files from {LegacyGameId} to {GameId}",
                    legacyId, gameId);
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
