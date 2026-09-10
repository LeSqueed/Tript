// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.App;

internal static class MuxerHelper
{
    private const string HelperFileName = "obs-ffmpeg-mux";

    internal static string? EnsureNextToApp(string? obsModuleBinaryDir = null)
    {
        if (OperatingSystem.IsWindows())
        {
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
            File.Delete(target);
            File.CreateSymbolicLink(target, systemHelper);
            return target;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
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

    private static IEnumerable<string?> CandidatePaths(string? obsModuleBinaryDir)
    {
        yield return SearchPath(HelperFileName);

        foreach (var dir in new[] { "/usr/bin", "/usr/local/bin" })
            yield return Path.Combine(dir, HelperFileName);

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
