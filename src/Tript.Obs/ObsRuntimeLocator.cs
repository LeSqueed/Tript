// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using System.Runtime.InteropServices;

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
                return new ObsRuntimeLocations(runtimeDir, moduleBinary, moduleData, FindLinuxCoreDataDir());
        }

        var pkgConfigDir = PkgConfigLibDir();
        if (pkgConfigDir is not null)
        {
            var moduleBinary = FindLinuxModuleBinaryDir(pkgConfigDir, out var moduleData);
            if (moduleBinary is not null)
                return new ObsRuntimeLocations(pkgConfigDir, moduleBinary, moduleData, FindLinuxCoreDataDir());
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
                return new ObsRuntimeLocations(candidate, moduleBinary, moduleData, FindLinuxCoreDataDir());
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

    private static string? FindLinuxCoreDataDir()
    {
        foreach (var candidate in new[]
        {
            "/usr/share/obs/obs-studio/",
            "/usr/share/obs/",
        })
        {
            if (Directory.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string? FindLinuxModuleDataDir(string libDir)
    {
        foreach (var candidate in new[]
        {
            "/usr/share/obs/obs-plugins",
            "/usr/share/obs/obs-studio/plugins",
        })
        {
            if (Directory.Exists(candidate))
                return candidate;
        }

        return null;
    }

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
            return null;
        }
    }
}
