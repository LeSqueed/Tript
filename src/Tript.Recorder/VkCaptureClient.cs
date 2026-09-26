// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Recorder;

internal static class VkCaptureClient
{
    private const string EnableVariable = "OBS_VKCAPTURE";
    private const string DisableVariable = "DISABLE_OBS_VKCAPTURE";
    private const string NameVariable = "OBS_VKCAPTURE_NAME";

    private static readonly HashSet<string> WinePreloaders = new(StringComparer.Ordinal)
    {
        "wine-preloader", "wine64-preloader",
    };

    internal static bool? IsLoadedInto(IProcessFiles files, int processId)
    {
        if (files.ReadExecutableLink(processId) is null)
            return null;

        return files.ReadEnvironmentVariable(processId, EnableVariable) == "1"
               && files.ReadEnvironmentVariable(processId, DisableVariable) != "1";
    }

    internal static string? NameOf(IProcessFiles files, int processId)
    {
        if (files.ReadEnvironmentVariable(processId, NameVariable) is { } customName)
            return customName;

        if (files.ReadExecutableLink(processId) is not { Length: > 0 } executable)
            return null;

        var name = Path.GetFileName(executable);
        return WinePreloaders.Contains(name) ? files.ReadCommandName(processId) : name;
    }
}
