// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;

namespace Tript.Settings;

// Where recordings are written by default. The Recording page's OutputDirectory setting is empty
// when the user has not chosen a location; the host (and the settings UI's hint) use this single
// canonical default so the two never disagree and the default is defined once.
public static class RecordingLocations
{
    // The platform default recordings directory, never empty:   Windows: %UserProfile%\Videos\Tript
    // (the Videos folder when it exists, else the profile)   Linux:   $XDG_VIDEOS_DIR/Tript
    // (XDG is empty unless the desktop sets it), else            ~/Videos/Tript
    public static string DefaultDirectory()
    {
        var videos = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? MyVideosOrProfile()
            : XdgVideosOrHomeVideos();

        return Path.Combine(videos, SettingsFilePaths.DirectoryName);
    }

    // The Videos special folder on Windows, falling back to the user profile when the folder is
    // not known (some Windows installs and most non-Windows runs of a Windows-targeted build).
    private static string MyVideosOrProfile()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (!string.IsNullOrEmpty(videos))
            return videos;

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile) ? Path.GetTempPath() : profile;
    }

    // $XDG_VIDEOS_DIR when the desktop sets it, else ~/Videos. The XDG variable is empty on a
    // headless run; the fallback keeps the default meaningful on a bare Linux install.
    private static string XdgVideosOrHomeVideos()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_VIDEOS_DIR");
        if (!string.IsNullOrEmpty(xdg))
            return xdg;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? Path.GetTempPath() : Path.Combine(home, "Videos");
    }
}
