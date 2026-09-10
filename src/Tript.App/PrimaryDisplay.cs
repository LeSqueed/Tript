// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;

namespace Tript.App;

internal readonly record struct DisplaySize(int Width, int Height)
{
    internal bool IsUsable => Width > 0 && Height > 0;

    public override string ToString() => $"{Width}x{Height}";
}

internal static class PrimaryDisplay
{
    internal static readonly DisplaySize Fallback = new(1920, 1080);

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
            return null;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Tript.App: could not detect the primary display ({exception.Message}); " +
                                    $"using {Fallback}.");
            return null;
        }
    }

    internal static DisplaySize DetectOrFallback() => Detect() ?? Fallback;

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

    private static DisplaySize? DetectX11()
    {
        if (XInitThreads() == 0)
            return null;

        var display = XOpenDisplay(null);
        if (display == nint.Zero)
            return null;

        try
        {
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

    [DllImport("libX11.so.6")]
    private static extern nint XDefaultRootWindow(nint display);

    [DllImport("libXrandr.so.2")]
    private static extern int XRRQueryVersion(nint display, out int major, out int minor);

    [DllImport("libXrandr.so.2")]
    private static extern nint XRRGetMonitors(nint display, nint window, int getActive, out int monitorCount);

    [DllImport("libXrandr.so.2")]
    private static extern void XRRFreeMonitors(nint monitors);
}
