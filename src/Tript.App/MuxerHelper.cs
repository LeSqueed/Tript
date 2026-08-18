// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.App;

// The ffmpeg_muxer plugin records by spawning the external obs-ffmpeg-mux helper, which it locates
// with os_get_executable_path_ptr — resolved to the *actual binary* of the current process, not the
// current directory. So the helper must sit next to the Tript.App executable at run time.
internal static class MuxerHelper
{
    private const string HelperFileName = "obs-ffmpeg-mux";

    // Resolves and links the helper beside the current executable. Best-effort: a machine where the
    // system helper is missing reports that the helper could not be provided, and the plugin itself
    // will surface the missing helper when a recording starts.
    internal static string? EnsureNextToApp(string? obsModuleBinaryDir = null)
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

        var systemHelper = ResolveSystemHelper(obsModuleBinaryDir);
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

    internal static string? ResolveSystemHelper(string? obsModuleBinaryDir = null)
    {
        foreach (var candidate in CandidatePaths(obsModuleBinaryDir))
        {
            if (candidate is not null && File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    // Where the helper actually lands. It is NOT on PATH on any mainstream distro: OBS ships it as a
    // private helper under the plugin directory, and on Debian/Ubuntu it is one level deeper still,
    // in a per-plugin subdirectory (/usr/lib/x86_64-linux-gnu/obs-plugins/obs-ffmpeg/obs-ffmpeg-mux).
    // Probing only PATH and the bin dirs found nothing there, so real recording failed with the
    // plugin's own "helper missing" error on a machine that had the helper installed all along.
    private static IEnumerable<string?> CandidatePaths(string? obsModuleBinaryDir)
    {
        yield return SearchPath(HelperFileName);

        foreach (var dir in new[] { "/usr/bin", "/usr/local/bin" })
            yield return Path.Combine(dir, HelperFileName);

        // The module directory the OBS locator resolved for this machine is the authoritative answer;
        // the fixed list below only covers a host that could not discover one.
        foreach (var dir in Enumerable.Repeat(obsModuleBinaryDir, 1).Concat(FallbackPluginDirs()))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;

            yield return Path.Combine(dir, HelperFileName);
            yield return Path.Combine(dir, "obs-ffmpeg", HelperFileName);
        }
    }

    private static IEnumerable<string> FallbackPluginDirs() =>
    [
        "/usr/lib/x86_64-linux-gnu/obs-plugins",
        "/usr/lib64/obs-plugins",
        "/usr/lib/obs-plugins",
        "/usr/local/lib/obs-plugins",
        "/usr/lib/x86_64-linux-gnu/obs-studio/plugins",
        "/usr/lib64/obs-studio/plugins",
        "/usr/lib/obs-studio/plugins",
    ];

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
