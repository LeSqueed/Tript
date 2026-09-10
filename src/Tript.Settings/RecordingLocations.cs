// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;

namespace Tript.Settings;

public static class RecordingLocations
{
    public static string DefaultDirectory()
    {
        var videos = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? MyVideosOrProfile()
            : XdgVideosOrHomeVideos();

        return Path.Combine(videos, SettingsFilePaths.DirectoryName);
    }

    private static string MyVideosOrProfile()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (!string.IsNullOrEmpty(videos))
            return videos;

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile) ? Path.GetTempPath() : profile;
    }

    private static string XdgVideosOrHomeVideos()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_VIDEOS_DIR");
        if (!string.IsNullOrEmpty(xdg))
            return xdg;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? Path.GetTempPath() : Path.Combine(home, "Videos");
    }
}
