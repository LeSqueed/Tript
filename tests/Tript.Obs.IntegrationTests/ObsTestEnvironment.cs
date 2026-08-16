// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;

namespace Tript.Obs.IntegrationTests;

// Everything the tests need to know about this machine's OBS install, in one place so a different
// runtime layout is a one-file change.
internal static class ObsTestEnvironment
{
    internal const string PluginBinaryPath = "/usr/lib/obs-plugins";
    internal const string PluginDataPath = "/usr/share/obs/obs-studio/plugins/%module%/data";
    internal const string CoreDataPath = "/usr/share/obs/obs-studio/";

    // An allowlist, not a preference. With no safe list libobs loads every plugin it finds, and
    // frontend-tools aborts the process on a machine with no Qt frontend to call back into —
    // measured, not guessed. Naming what we need keeps the tests to plugins that can run headless.
    internal static readonly string[] SafeModules =
    [
        "obs-x264",
        "obs-ffmpeg",
        "linux-capture",
        "image-source"
    ];

    // The modules the audio-routing tests load on top of the base set. linux-pulseaudio registers
    // the audio device capture sources (pulse_input_capture / pulse_output_capture) that the
    // multi-track routing drives; the base list deliberately omits it because the recording
    // milestone never needs an audio device, and a machine with no PulseAudio server should not be
    // prevented from loading the modules it does need.
    internal static readonly string[] AudioModules =
    [
        "linux-pulseaudio"
    ];

    private static readonly Lock DisplayGate = new();
    private static nint _display;

    // libobs cannot discover the display server for itself on Linux, so the host opens one and
    // hands the pointer over. Opened once and never closed: libobs binds an EGL display to it and
    // closing it underneath a live graphics module is a crash, while process exit reclaims it.
    internal static nint XDisplay
    {
        get
        {
            lock (DisplayGate)
            {
                if (_display != nint.Zero)
                    return _display;

                // Xlib is single-threaded until told otherwise, and libobs drives the same display
                // from its graphics thread as well as from whichever thread called into it — which
                // under a test runner is a different thread per test. Without this the process dies
                // inside Xlib after a couple of dozen contexts, and it must come before the open.
                if (XInitThreads() == 0)
                    throw new InvalidOperationException("XInitThreads failed; Xlib cannot be used from more than one thread.");

                _display = XOpenDisplay(null);
                if (_display == nint.Zero)
                    throw new InvalidOperationException(
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
