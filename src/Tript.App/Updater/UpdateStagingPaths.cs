// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.App.Updater;

// Staging lives under the install root so launcher.c's final rename stays on one volume.
internal static class UpdateStagingPaths
{
    private const string StagingDirectoryName = ".tript-update";
    private const string MarkerFileName = "ready.marker";
    private const string OldAppFolderName = "old-App";

    internal static string InstallRootFromAppBaseDirectory(string appBaseDirectory)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appBaseDirectory));
        return Path.GetDirectoryName(trimmed)
            ?? throw new InvalidOperationException("The app's install root could not be determined.");
    }

    internal static string StagingRoot(string installRoot) => Path.Combine(installRoot, StagingDirectoryName);

    internal static string MarkerPath(string installRoot) => Path.Combine(StagingRoot(installRoot), MarkerFileName);

    internal static string OldAppBackupPath(string installRoot) =>
        Path.Combine(StagingRoot(installRoot), OldAppFolderName);

    internal static string DownloadPath(string installRoot, string token) =>
        Path.Combine(StagingRoot(installRoot), $"download-{token}.zip.part");

    internal static string StagedFolderPath(string installRoot, string stagedFolderName) =>
        Path.Combine(StagingRoot(installRoot), stagedFolderName);

    internal static string NewStagedFolderName(string token) => $"staged-{token}";
}
