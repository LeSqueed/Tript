// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;

namespace Tript.App;

internal static class NixPlatformSelector
{
    internal const string OverrideVariable = "TRIPT_DISPLAY_PLATFORM";

    internal static ObsNixPlatform Select(string? requested, string? waylandDisplay, string? x11Display,
        bool pipeWireModulePresent)
    {
        if (string.Equals(requested, "x11", StringComparison.OrdinalIgnoreCase))
            return ObsNixPlatform.X11Egl;
        if (string.Equals(requested, "wayland", StringComparison.OrdinalIgnoreCase))
            return ObsNixPlatform.Wayland;

        if (string.IsNullOrEmpty(waylandDisplay))
            return ObsNixPlatform.X11Egl;

        return pipeWireModulePresent || string.IsNullOrEmpty(x11Display)
            ? ObsNixPlatform.Wayland
            : ObsNixPlatform.X11Egl;
    }

    internal static ObsNixPlatform FromEnvironment(bool pipeWireModulePresent) =>
        Select(
            Environment.GetEnvironmentVariable(OverrideVariable),
            Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"),
            Environment.GetEnvironmentVariable("DISPLAY"),
            pipeWireModulePresent);
}
