// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using System.Runtime.InteropServices;
using Tript.Core;

namespace Tript.Obs;

public sealed record ObsRuntimeLocations(
    string? RuntimeDirectory,
    string? ModuleBinaryDir,
    string? ModuleDataDir,
    string? CoreDataDir,
    string? LibobsDataDir = null)
{
    public bool Found => ModuleBinaryDir is not null;
}

public static class ObsRuntimeLocator
{
    public const string RuntimeDirectoryVariable = "TRIPT_OBS_RUNTIME_DIR";

    public static ObsRuntimeLocations Discover()
    {
        if (OperatingSystem.IsWindows())
            return DiscoverWindows();
        return DiscoverLinux();
    }

    private static ObsRuntimeLocations DiscoverWindows()
    {
        var baseDir = AppContext.BaseDirectory;
        var runtimeDir = Path.GetFullPath(Path.Combine(baseDir, "bin", "64bit"));
        var moduleBinaryDir = Path.GetFullPath(Path.Combine(baseDir, "obs-plugins", "64bit"));
        var moduleDataDir = Path.GetFullPath(Path.Combine(baseDir, "data", "obs-plugins"));

        if (!File.Exists(Path.Combine(runtimeDir, "obs64.dll")) &&
            !File.Exists(Path.Combine(runtimeDir, "obs.dll")))
            return new ObsRuntimeLocations(null, null, null, null);

        var libobsDataDir = Path.GetFullPath(Path.Combine(baseDir, "data", "libobs"))
            .Replace('\\', '/') + "/";
        var coreDataDir = Path.GetFullPath(Path.Combine(baseDir, "data", "obs-studio"))
            .Replace('\\', '/') + "/";
        return new ObsRuntimeLocations(runtimeDir, moduleBinaryDir, moduleDataDir, coreDataDir, libobsDataDir);
    }

    private static ObsRuntimeLocations DiscoverLinux()
    {
        var envDir = Environment.GetEnvironmentVariable(RuntimeDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(envDir))
        {
            var runtimeDir = Path.GetFullPath(envDir);

            var moduleBinary = FindLinuxModuleBinaryDir(runtimeDir, out var moduleData);
            if (moduleBinary is not null)
                return new ObsRuntimeLocations(runtimeDir, moduleBinary, moduleData, FindLinuxCoreDataDir(runtimeDir));
        }

        var pkgConfigDir = PkgConfigLibDir();
        if (pkgConfigDir is not null)
        {
            var moduleBinary = FindLinuxModuleBinaryDir(pkgConfigDir, out var moduleData);
            if (moduleBinary is not null)
                return new ObsRuntimeLocations(pkgConfigDir, moduleBinary, moduleData, FindLinuxCoreDataDir(pkgConfigDir));
        }

        foreach (var candidate in new[]
        {
            "/usr/lib/x86_64-linux-gnu",
            "/usr/lib64",
            "/usr/lib",
            "/usr/local/lib",
        })
        {
            var moduleBinary = FindLinuxModuleBinaryDir(candidate, out var moduleData);
            if (moduleBinary is not null)
                return new ObsRuntimeLocations(candidate, moduleBinary, moduleData, FindLinuxCoreDataDir(candidate));
        }

        return new ObsRuntimeLocations(null, null, null, null);
    }

    private static string? FindLinuxModuleBinaryDir(string libDir, out string? dataDir)
    {
        dataDir = null;
        foreach (var pluginDir in new[]
        {
            Path.Combine(libDir, "obs-plugins"),
            Path.Combine(libDir, "obs-studio", "plugins"),
        })
        {
            if (!Directory.Exists(pluginDir))
                continue;

            if (!Directory.EnumerateFiles(pluginDir, "*.so").Any())
                continue;

            dataDir = FindLinuxModuleDataDir(libDir);
            return pluginDir;
        }

        return null;
    }

    private const string SystemShareRoot = "/usr/share/obs";

    internal static string? InstallPrefixOf(string libDir)
    {
        var segments = libDir.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = segments.Length - 1; index >= 0; index--)
        {
            if (segments[index].StartsWith("lib", StringComparison.Ordinal))
                return "/" + string.Join('/', segments[..index]);
        }

        return null;
    }

    internal static IReadOnlyList<string> LinuxShareRoots(string libDir)
    {
        var roots = new List<string>();
        if (InstallPrefixOf(libDir) is { } prefix)
            roots.Add(prefix.TrimEnd('/') + "/share/obs");
        if (!roots.Contains(SystemShareRoot, StringComparer.Ordinal))
            roots.Add(SystemShareRoot);
        return roots;
    }

    private static string? FindLinuxCoreDataDir(string libDir) =>
        FirstExisting(LinuxShareRoots(libDir).SelectMany(root => new[]
        {
            Path.Combine(root, "obs-studio") + "/",
            root + "/",
        }));

    private static string? FindLinuxModuleDataDir(string libDir) =>
        FirstExisting(LinuxShareRoots(libDir).SelectMany(root => new[]
        {
            Path.Combine(root, "obs-plugins"),
            Path.Combine(root, "obs-studio", "plugins"),
        }));

    private static string? FirstExisting(IEnumerable<string> candidates) =>
        candidates.FirstOrDefault(Directory.Exists);

    private static string? PkgConfigLibDir()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pkg-config",
                ArgumentList = { "--variable=libdir", "libobs" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi);
            if (process is null)
                return null;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(500);
            return process.HasExited && process.ExitCode == 0 && !string.IsNullOrEmpty(output)
                ? output
                : null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Returning null surfaces later only as "No OBS runtime was found", so the real reason,
            // usually pkg-config missing or failing, has to be recorded here.
            Diagnostics.Report(DiagnosticLevel.Warning, "Probing for the OBS runtime failed", exception);
            return null;
        }
    }
}
