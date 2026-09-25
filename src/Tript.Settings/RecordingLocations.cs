// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;
using SpecialFolder = System.Environment.SpecialFolder;
using SpecialFolderOption = System.Environment.SpecialFolderOption;

namespace Tript.Settings;

public static class RecordingLocations
{
    public static string DefaultDirectory() =>
        DefaultDirectory(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), Environment.GetFolderPath,
            Path.GetTempPath());

    internal static string DefaultDirectory(bool windows,
        Func<SpecialFolder, SpecialFolderOption, string> folderPath, string tempPath)
    {
        var videos = windows
            ? MyVideosOrProfile(folderPath, tempPath)
            : UserVideosOrHomeVideos(folderPath, tempPath);

        return Path.Combine(videos, SettingsFilePaths.DirectoryName);
    }

    private static string MyVideosOrProfile(Func<SpecialFolder, SpecialFolderOption, string> folderPath,
        string tempPath)
    {
        var videos = folderPath(SpecialFolder.MyVideos, SpecialFolderOption.None);
        if (!string.IsNullOrEmpty(videos))
            return videos;

        var profile = folderPath(SpecialFolder.UserProfile, SpecialFolderOption.None);
        return string.IsNullOrEmpty(profile) ? tempPath : profile;
    }

    private static string UserVideosOrHomeVideos(Func<SpecialFolder, SpecialFolderOption, string> folderPath,
        string tempPath)
    {
        var videos = folderPath(SpecialFolder.MyVideos, SpecialFolderOption.DoNotVerify);
        if (!string.IsNullOrEmpty(videos))
            return videos;

        var home = folderPath(SpecialFolder.UserProfile, SpecialFolderOption.DoNotVerify);
        return string.IsNullOrEmpty(home) ? tempPath : Path.Combine(home, "Videos");
    }
}
