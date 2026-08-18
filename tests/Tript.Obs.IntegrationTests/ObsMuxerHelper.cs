// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.App;

namespace Tript.Obs.IntegrationTests;

// The ffmpeg_muxer plugin records by spawning the external obs-ffmpeg-mux helper, which it locates
// with os_get_executable_path_ptr — resolved to the *actual binary* of the current process, not the
// current directory (measured on this box with a standalone probe: a copy and a symlink of the
// probe both resolved). The recording harness (Tript.RecorderHarness) is the process that starts
// the output, so the helper must sit next to the harness binary in the output directory.
internal static class ObsMuxerHelper
{
    // The name the plugin spawns. It is the helper's file name, not a library name, so no
    // extension-less translation applies.
    internal const string HelperFileName = "obs-ffmpeg-mux";

    // Asks the app's own resolver rather than restating where the helper lives. OBS ships it as a
    // private plugin helper, not on PATH — on Debian one level deeper still, under
    // obs-plugins/obs-ffmpeg — and a second copy of that list is what reported "could not be
    // deployed" on a machine that had the helper installed all along.
    internal static string? SystemHelperPath() =>
        MuxerHelper.ResolveSystemHelper(ObsRuntimeLocator.Discover().ModuleBinaryDir);

    // Puts the helper in one named directory — the harness's. Guessing at candidate directories is
    // how a copy landed beside the *test* process (~/.dotnet) and satisfied nothing.
    internal static bool TryDeploy(string directory)
    {
        var target = Path.Combine(directory, HelperFileName);
        if (File.Exists(target))
            return true;

        var systemHelper = SystemHelperPath();
        if (systemHelper is null)
            return false;

        try
        {
            File.Copy(systemHelper, target, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A read-only output directory is not a recording failure yet — the plugin itself will
            // report the missing helper when the output starts.
            return false;
        }
    }
}
