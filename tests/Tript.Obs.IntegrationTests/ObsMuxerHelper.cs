// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;

namespace Tript.Obs.IntegrationTests;

// The ffmpeg_muxer plugin records by spawning the external obs-ffmpeg-mux helper, which it locates
// with os_get_executable_path_ptr — resolved to the *actual binary* of the current process,
// not the current directory (measured on this box with a standalone probe: a copy and a symlink of
// the probe both resolved). The recording harness (Tript.RecorderHarness) is the process that starts
// the output, so the helper must sit next to the harness binary in the output directory. A dev box
// running OBS has it at /usr/bin, which is nowhere on that path, so this places a copy there before
// a recording starts.
//
// Both the copy and the file-layout discovery are kept *out* of the fixtures' happy path: a build
// layout that already satisfies the plugin (or a machine where the copy cannot be made) is not an
// error, and the whole machinery is what a recording pipeline would replace with a real install.
internal static class ObsMuxerHelper
{
    // The name the plugin spawns. It is the helper's file name, not a library name, so no
    // extension-less translation applies.
    internal const string HelperFileName = "obs-ffmpeg-mux";

    // The directories, resolved, in which the plugin's os_get_executable_path_ptr will look for the
    // helper once a recording starts. Any one of them containing a runnable helper satisfies it.
    internal static IReadOnlyList<string> CandidateDirectories()
    {
        var directories = new List<string>();

        var processDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        if (processDirectory is not null)
            directories.Add(processDirectory);

        var appContextDirectory = Path.GetDirectoryName(AppContext.BaseDirectory);
        if (appContextDirectory is not null && !directories.Contains(appContextDirectory, StringComparer.Ordinal))
            directories.Add(appContextDirectory);

        var assemblyDirectory = Path.GetDirectoryName(typeof(ObsMuxerHelper).Assembly.Location);
        if (assemblyDirectory is not null && !directories.Contains(assemblyDirectory, StringComparer.Ordinal))
            directories.Add(assemblyDirectory);

        return directories;
    }

    internal static bool IsSatisfied()
    {
        foreach (var directory in CandidateDirectories())
        {
            var candidate = Path.Combine(directory, HelperFileName);
            if (File.Exists(candidate))
                return true;
        }

        return false;
    }

    // The directories to search for a system install of the helper, best-effort. Not asserting on
    // their existence: they are where OBS installs on a Linux distro, and the machine this suite
    // runs on happens to have it at /usr/bin.
    internal static IReadOnlyList<string> SystemInstallDirectories() =>
        ["/usr/bin", "/usr/local/bin"];

    internal static bool TryDeploy()
    {
        if (IsSatisfied())
            return true;

        string? systemHelper = SystemInstallDirectories()
            .Select(directory => Path.Combine(directory, HelperFileName))
            .FirstOrDefault(File.Exists);

        if (systemHelper is null)
            return false;

        foreach (var directory in CandidateDirectories())
        {
            var target = Path.Combine(directory, HelperFileName);
            if (File.Exists(target))
                continue;

            try
            {
                File.Copy(systemHelper, target, overwrite: true);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A read-only output directory is not a recording failure yet — the next candidate
                // may work, and if none does the plugin itself will report the missing helper.
            }
        }

        return false;
    }
}
