// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.App;

// The ffmpeg_muxer plugin records by spawning the external obs-ffmpeg-mux helper, which it locates
// with os_get_executable_path_ptr — resolved to the *actual binary* of the current process, not the
// current directory. So the helper must sit next to the Tript.App executable at run time.
//
// The helper is part of the OBS install. On Linux it lives in the system OBS package (typically
// /usr/bin/obs-ffmpeg-mux); the app does not ship it, so instead of copying a binary out of the
// package we create a symlink beside our own executable pointing at the system helper. A symlink
// keeps the link honest about where the real file lives, and if the OBS package moves the helper
// the next launch re-resolves it. On Windows the bundled OBS runtime already has obs-ffmpeg-mux.exe
// beside the app, so nothing is done.
internal static class MuxerHelper
{
    private const string HelperFileName = "obs-ffmpeg-mux";

    // Resolves and links the helper beside the current executable. Best-effort: a machine where the
    // system helper is missing reports that the helper could not be provided, and the plugin itself
    // will surface the missing helper when a recording starts.
    internal static string? EnsureNextToApp()
    {
        if (OperatingSystem.IsWindows())
        {
            // The bundled layout already places obs-ffmpeg-mux.exe next to the app. Nothing to do.
            var bundled = Path.Combine(ProcessDirectory(), HelperFileName + ".exe");
            return File.Exists(bundled) ? bundled : null;
        }

        var target = Path.Combine(ProcessDirectory(), HelperFileName);
        if (File.Exists(target) || File.Exists(target + ".symlink"))
            return target;

        var systemHelper = ResolveSystemHelper();
        if (systemHelper is null)
            return null;

        try
        {
            // Remove any stale entry first (a previous failed link, or a package that was removed).
            File.Delete(target);
            File.CreateSymbolicLink(target, systemHelper);
            return target;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // The app directory is read-only, or the filesystem does not support symlinks. A real
            // install would place the helper beside the app already; failing to link is not a
            // startup failure, the plugin reports it when recording.
            return null;
        }
    }

    private static string? ResolveSystemHelper()
    {
        // PATH first — the distro may install it there.
        var fromPath = SearchPath(HelperFileName);
        if (fromPath is not null)
            return fromPath;

        // Then standard dirs (the OBS package layout).
        foreach (var dir in new[] { "/usr/bin", "/usr/local/bin" })
        {
            var candidate = Path.Combine(dir, HelperFileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string? SearchPath(string fileName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
            return null;

        foreach (var dir in pathValue.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;
            var candidate = Path.Combine(dir.Trim(), fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string ProcessDirectory() =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
}
