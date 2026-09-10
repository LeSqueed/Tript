// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;
using Tript.Obs;
using Tript.Obs.Interop;

namespace Tript.Obs.IntegrationTests;

internal static class ObsTestEnvironment
{
    private static readonly Lazy<ObsRuntimeLocations> Loc = new(ObsRuntimeLocator.Discover);

    internal static void RequireUsableRuntime()
    {
        if (!OperatingSystem.IsLinux())
            throw new Xunit.SkipException("The OBS integration suite requires the Linux libobs runtime and X11/XWayland.");

        if (!Loc.Value.Found)
            throw new Xunit.SkipException("No OBS runtime found; the integration tests need a system obs-studio install.");

        try
        {
            ObsLibrary.EnsureLoaded();
        }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            throw new Xunit.SkipException($"The discovered OBS runtime could not be loaded: {exception.Message}");
        }
    }

    internal static string PluginBinaryPath =>
        Loc.Value.ModuleBinaryDir
        ?? throw new Xunit.SkipException("No OBS runtime found; the integration tests need a system obs-studio install.");

    internal static string PluginDataPath =>
        Path.Combine(Loc.Value.ModuleDataDir ?? PluginBinaryPath, "%module%");

    internal static string ModuleDataDir =>
        Loc.Value.ModuleDataDir ?? PluginBinaryPath;

    internal static string? CoreDataPath => Loc.Value.CoreDataDir;

    internal static readonly string[] SafeModules =
    [
        "obs-x264",
        "obs-ffmpeg",
        "linux-capture",
        "image-source"
    ];

    internal static readonly string[] AudioModules =
    [
        "linux-pulseaudio"
    ];

    private static readonly Lock DisplayGate = new();
    private static nint _display;

    internal static nint XDisplay
    {
        get
        {
            lock (DisplayGate)
            {
                if (_display != nint.Zero)
                    return _display;

                if (XInitThreads() == 0)
                    throw new InvalidOperationException("XInitThreads failed; Xlib cannot be used from more than one thread.");

                _display = XOpenDisplay(null);
                if (_display == nint.Zero)
                    throw new Xunit.SkipException(
                        "XOpenDisplay returned null. These tests reset real video through libobs-opengl, " +
                        "which needs a reachable X server (DISPLAY, or XWayland under a Wayland session).");

                return _display;
            }
        }
    }

    [DllImport("libX11.so.6", CharSet = CharSet.Ansi)]
    private static extern nint XOpenDisplay(string? name);

    [DllImport("libX11.so.6")]
    private static extern int XInitThreads();
}
