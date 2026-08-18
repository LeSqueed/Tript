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
    // multi-monitor desktop. Deliberately not GetSystemMetrics(SM_CXSCREEN/SM_CYSCREEN) as the
    // first choice: those are DPI-virtualized.
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

    // RandR's monitor list, not the X screen. **The measured distinction that makes RandR
    // necessary.** DisplayWidth/DisplayHeight (and therefore anything that reads "the screen") give
    // the bounding box of every monitor joined together.
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
