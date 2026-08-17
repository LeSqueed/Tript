// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;

namespace Tript.App;

// The pixel size of a display. A record struct rather than a tuple because it crosses three layers
// (detection, the first-run settings default, and the settings push the UI reads).
internal readonly record struct DisplaySize(int Width, int Height)
{
    // A detected size of zero is not a display — it is a driver or an X server answering with
    // nothing. Every consumer checks this before treating the value as a resolution.
    internal bool IsUsable => Width > 0 && Height > 0;

    public override string ToString() => $"{Width}x{Height}";
}

// Where the primary display's resolution comes from. Two things need it: the resolution a fresh
// install defaults to, and the "(display)" option the resolution selector offers.
//
// **Detection never throws and never propagates a platform failure.** A machine we cannot read a
// display from is a machine that records at the fallback resolution, which is a working recording;
// a wrong answer here becomes the OBS canvas, and a canvas of the wrong size is a broken recording
// on every machine it happens to. So every path returns null on doubt and the caller falls back.
internal static class PrimaryDisplay
{
    // 1080p: the resolution to record at when the machine will not say what it has. It is the
    // modal gaming resolution and it is a size every encoder on every machine can handle.
    internal static readonly DisplaySize Fallback = new(1920, 1080);

    // The primary display's resolution, or null when it could not be determined.
    internal static DisplaySize? Detect()
    {
        try
        {
            var detected = OperatingSystem.IsWindows() ? DetectWindows() : DetectX11();
            return detected is { IsUsable: true } size ? size : null;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException
                                              or BadImageFormatException or ExternalException)
        {
            // A missing or older native library (no libXrandr, an X server without RandR 1.5) is a
            // detection failure, not a startup failure.
            return null;
        }
        catch (Exception exception)
        {
            // Anything else is still not worth failing startup over — but it is worth saying out
            // loud, because a detector that silently returns the fallback on every machine looks
            // exactly like a detector that works.
            Console.Error.WriteLine($"Tript.App: could not detect the primary display ({exception.Message}); " +
                                    $"using {Fallback}.");
            return null;
        }
    }

    internal static DisplaySize DetectOrFallback() => Detect() ?? Fallback;

    // ---- Windows ----

    // GDI's DESKTOPHORZRES/DESKTOPVERTRES on the screen DC, which is the primary monitor's DC on a
    // multi-monitor desktop.
    //
    // Deliberately not GetSystemMetrics(SM_CXSCREEN/SM_CYSCREEN) as the first choice: those are
    // DPI-virtualized. A process that has not declared per-monitor DPI awareness is told the
    // *logical* size — a 3840x2160 display at 150% scaling reports 2560x1440 — and that number
    // would become the canvas, so the recording would be a scaled 2560x1440 of a 4K screen without
    // anything saying so. DESKTOPHORZRES/DESKTOPVERTRES report the real mode regardless of the
    // process's awareness, which is the documented way to get physical pixels without changing
    // process-wide DPI state (the shell owns that, and changing it here would move its window).
    //
    // SM_CXSCREEN stays as the second choice: it is wrong by the DPI scale factor at worst, which
    // still beats no answer at all.
    //
    // Unverified on Windows hardware — the Windows tier of the test strategy is human-run
    // (CLAUDE.md "Testing"). Both routes fail soft, so the worst case here is the 1080p fallback.
    private static DisplaySize? DetectWindows()
    {
        var hdc = GetDC(nint.Zero);
        if (hdc != nint.Zero)
        {
            try
            {
                var physical = new DisplaySize(
                    GetDeviceCaps(hdc, DesktopHorzRes),
                    GetDeviceCaps(hdc, DesktopVertRes));
                if (physical.IsUsable)
                    return physical;
            }
            finally
            {
                ReleaseDC(nint.Zero, hdc);
            }
        }

        var logical = new DisplaySize(GetSystemMetrics(SmCxScreen), GetSystemMetrics(SmCyScreen));
        return logical.IsUsable ? logical : null;
    }

    private const int SmCxScreen = 0;

    private const int SmCyScreen = 1;

    private const int DesktopVertRes = 117;

    private const int DesktopHorzRes = 118;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint hdc);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(nint hdc, int index);

    // ---- Linux / X11 ----

    // RandR's monitor list, not the X screen.
    //
    // **The measured distinction that makes RandR necessary.** DisplayWidth/DisplayHeight (and
    // therefore anything that reads "the screen") give the bounding box of every monitor joined
    // together. On the development machine — DP-1 1920x1080 at +0+0, DP-2 2560x1440 at +1920+0,
    // HDMI-A-1 1920x1080 at +4480+0 — the X screen is 6400x1440. Handing that to obs_reset_video
    // would allocate a canvas nearly three and a half times the intended area and record a picture
    // no display has. The monitor we want is DP-2's 2560x1440.
    //
    // XRRGetMonitors (RandR 1.5) is the whole answer in one call: it reports each active monitor's
    // geometry *and* which one is primary, so there is no walk over outputs and CRTCs to get the
    // same facts.
    //
    // **Also measured: the primary flag is frequently not set at all.** On this machine (Xwayland,
    // three monitors) XRRGetOutputPrimary returns None and every monitor comes back with
    // primary = 0 — `xrandr` prints no "primary" marker either. So an implementation that only
    // honours the flag detects nothing on a very ordinary desktop. When no monitor claims to be
    // primary, the largest by pixel area is chosen: it is the display worth recording at, and it is
    // stable across runs (unlike "the first one" or "the one at the origin", which follow the
    // monitor ordering and the layout rather than anything the user cares about).
    private static DisplaySize? DetectX11()
    {
        // Xlib's rule is that XInitThreads comes before any other Xlib call in the process. The app
        // host opens its own display for libobs later (Program.StartObsRuntime) and calls it there
        // too; repeat calls are a no-op, so satisfying the rule here costs nothing and keeps this
        // detector safe to run first.
        if (XInitThreads() == 0)
            return null;

        var display = XOpenDisplay(null);
        if (display == nint.Zero)
            return null;

        try
        {
            // The extension check is explicit so an X server without RandR is a null rather than a
            // trip through the error handler.
            if (XRRQueryVersion(display, out var major, out var minor) == 0)
                return null;
            if (major < 1 || (major == 1 && minor < 5))
                return null;

            return LargestOrPrimaryMonitor(display);
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    private static DisplaySize? LargestOrPrimaryMonitor(nint display)
    {
        // getActive = 1: only monitors that are switched on and have geometry. A disabled output has
        // no resolution to record at.
        var monitors = XRRGetMonitors(display, XDefaultRootWindow(display), 1, out var count);
        if (monitors == nint.Zero || count <= 0)
            return null;

        try
        {
            var stride = Marshal.SizeOf<XRRMonitorInfo>();
            DisplaySize? largest = null;
            long largestArea = 0;

            for (var index = 0; index < count; index++)
            {
                var monitor = Marshal.PtrToStructure<XRRMonitorInfo>(monitors + (index * stride));
                var size = new DisplaySize(monitor.Width, monitor.Height);
                if (!size.IsUsable)
                    continue;

                // A monitor that claims to be primary is the answer outright — RandR permits at
                // most one, and the user's choice of primary beats any heuristic of ours.
                if (monitor.Primary != 0)
                    return size;

                var area = (long)size.Width * size.Height;
                if (area > largestArea)
                {
                    largestArea = area;
                    largest = size;
                }
            }

            return largest;
        }
        finally
        {
            XRRFreeMonitors(monitors);
        }
    }

    // XRRMonitorInfo (Xrandr.h). Atom and RROutput* are pointer-width, Bool is a C int; the field
    // order is the header's, so sequential layout with natural alignment matches it (56 bytes on
    // LP64: an 8-byte Atom, ten 4-byte ints, four bytes of padding, an 8-byte pointer).
    [StructLayout(LayoutKind.Sequential)]
    private struct XRRMonitorInfo
    {
        public nint Name;

        public int Primary;

        public int Automatic;

        public int OutputCount;

        public int X;

        public int Y;

        public int Width;

        public int Height;

        public int MillimetreWidth;

        public int MillimetreHeight;

        public nint Outputs;
    }

    [DllImport("libX11.so.6", CharSet = CharSet.Ansi)]
    private static extern nint XOpenDisplay(string? name);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(nint display);

    [DllImport("libX11.so.6")]
    private static extern int XInitThreads();

    // The function form of the RootWindow macro — a macro is not something P/Invoke can reach.
    [DllImport("libX11.so.6")]
    private static extern nint XDefaultRootWindow(nint display);

    [DllImport("libXrandr.so.2")]
    private static extern int XRRQueryVersion(nint display, out int major, out int minor);

    [DllImport("libXrandr.so.2")]
    private static extern nint XRRGetMonitors(nint display, nint window, int getActive, out int monitorCount);

    [DllImport("libXrandr.so.2")]
    private static extern void XRRFreeMonitors(nint monitors);
}
