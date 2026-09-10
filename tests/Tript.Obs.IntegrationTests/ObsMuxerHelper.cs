// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.App;

namespace Tript.Obs.IntegrationTests;

internal static class ObsMuxerHelper
{
    internal const string HelperFileName = "obs-ffmpeg-mux";

    internal static string? SystemHelperPath() =>
        MuxerHelper.ResolveSystemHelper(ObsRuntimeLocator.Discover().ModuleBinaryDir);

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
            return false;
        }
    }
}
