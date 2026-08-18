// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Tript.Obs;

// Locates an installed (Linux) or bundled (Windows) OBS runtime so the app does not hardcode a
// distro layout. The app calls Discover once at startup and feeds the result into
// ObsRuntime.SetRuntimeDirectory, AddModulePath, and the module allowlist.
//
// Linux: OBS is a system dependency. The discovery probes, in order:
//   1. TRIPT_OBS_RUNTIME_DIR (when set) — an explicitly bundled runtime.
//   2. pkg-config --variable=libdir libobs — the distro's own answer for where libobs lives.
//   3. Standard candidate dirs — Debian/Ubuntu use /usr/lib/x86_64-linux-gnu, Fedora /usr/lib64,
//      Arch /usr/lib. The data dir is derived from the found binary dir by looking for the
//      obs-plugins data layout.
// Windows: OBS is bundled next to the app. The layout is the official OBS Studio Windows
// structure: bin/64bit/obs64.dll, obs-plugins/64bit/*.dll, data/obs-plugins/*/data.
//
// The discovery is best-effort: it returns the first plausible candidate. A machine without OBS
// reports no candidate and the caller decides how to fail.
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
        // Bundled layout under the app's base directory.
        var baseDir = AppContext.BaseDirectory;
        var runtimeDir = Path.GetFullPath(Path.Combine(baseDir, "bin", "64bit"));
        var moduleBinaryDir = Path.GetFullPath(Path.Combine(baseDir, "obs-plugins", "64bit"));
        var moduleDataDir = Path.GetFullPath(Path.Combine(baseDir, "data", "obs-plugins"));

        // The runtime dir must actually contain obs64.dll.
        if (!File.Exists(Path.Combine(runtimeDir, "obs64.dll")) &&
            !File.Exists(Path.Combine(runtimeDir, "obs.dll")))
            return new ObsRuntimeLocations(null, null, null, null);

        // Trailing separator so obs_find_data_file concatenates the root with the relative path.
        // The Windows portable layout splits libobs's own data across two sibling dirs under data/:
        // the effects live in data/libobs and the rest (locale, themes) in data/obs-studio. Both
        // are search roots, so both are returned; a distro install (Linux) has one obs-studio dir.
        //
        // Forward slashes, not the platform separator: libobs's effect preprocessor resolves a
        // #include by prepending the including file's directory to the include name, and it splits
        // on '/' specifically (cf-lexer.c insert_path). A data root that uses backslashes defeats
        // that split, the include resolves against the process CWD instead and the effect fails to
        // load — a Windows-only failure. Windows file APIs accept '/' paths, so both forms work
        // everywhere else.
        var libobsDataDir = Path.GetFullPath(Path.Combine(baseDir, "data", "libobs"))
            .Replace('\\', '/') + "/";
        var coreDataDir = Path.GetFullPath(Path.Combine(baseDir, "data", "obs-studio"))
            .Replace('\\', '/') + "/";
        return new ObsRuntimeLocations(runtimeDir, moduleBinaryDir, moduleDataDir, coreDataDir, libobsDataDir);
    }

    private static ObsRuntimeLocations DiscoverLinux()
    {
        // 1. Explicit override.
        var envDir = Environment.GetEnvironmentVariable(RuntimeDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(envDir))
        {
            var runtimeDir = Path.GetFullPath(envDir);
            // A TRIPT_OBS_RUNTIME_DIR points at a directory that contains libobs, and the module
            // layout is derived relative to it.
            var moduleBinary = FindLinuxModuleBinaryDir(runtimeDir, out var moduleData);
            if (moduleBinary is not null)
                return new ObsRuntimeLocations(runtimeDir, moduleBinary, moduleData, FindLinuxCoreDataDir());
        }

        // 2. pkg-config libobs gives the distro's libdir.
        var pkgConfigDir = PkgConfigLibDir();
        if (pkgConfigDir is not null)
        {
            var moduleBinary = FindLinuxModuleBinaryDir(pkgConfigDir, out var moduleData);
            if (moduleBinary is not null)
                return new ObsRuntimeLocations(pkgConfigDir, moduleBinary, moduleData, FindLinuxCoreDataDir());
        }

        // 3. Standard candidate dirs.
        foreach (var candidate in new[]
        {
            "/usr/lib/x86_64-linux-gnu",   // Debian/Ubuntu
            "/usr/lib64",                  // Fedora
            "/usr/lib",                    // Arch
            "/usr/local/lib",
        })
        {
            var moduleBinary = FindLinuxModuleBinaryDir(candidate, out var moduleData);
            if (moduleBinary is not null)
                return new ObsRuntimeLocations(candidate, moduleBinary, moduleData, FindLinuxCoreDataDir());
        }

        return new ObsRuntimeLocations(null, null, null, null);
    }

    // For a given libdir, find the obs-plugins binary dir and its sibling data dir. libobs
    // puts plugins under <libdir>/obs-plugins. The data dir is <datadir>/obs-plugins, which on
    // Debian/Arch is /usr/share/obs/obs-plugins (the /usr/share/obs/obs-studio/plugins form is
    // the OBS portable layout and is probed too).
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

            // The plugin dir must contain at least one *.so — an empty dir is not an install.
            if (!Directory.EnumerateFiles(pluginDir, "*.so").Any())
                continue;

            dataDir = FindLinuxModuleDataDir(libDir);
            return pluginDir;
        }

        return null;
    }

    // The core data dir: libobs's own effects, locale and licenses. Arch puts it at
    // /usr/share/obs/obs-studio; Debian keeps the obs-plugins data under /usr/share/obs/obs-plugins.
    // The trailing slash matters: obs_find_data_file concatenates the search root with the
    // relative path, so a root without a trailing separator joins onto the filename.
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
        // Data dirs mirror the binary layout: /usr/share/obs/obs-plugins and the portable
        // /usr/share/obs/obs-studio/plugins form.
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
            // pkg-config missing or not runnable.
            return null;
        }
    }
}
