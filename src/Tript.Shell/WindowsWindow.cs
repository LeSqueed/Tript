// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;
using Photino.NET;

namespace Tript.Shell;

internal static class WindowsWindow
{
    internal const int Show = 5;
    internal const int Hide = 0;
    internal const int Restore = 9;
    private const uint WmSysCommand = 0x0112;
    private const uint WmClose = 0x0010;
    private const int ScMinimize = 0xF020;
    private const uint MonitorDefaultToNull = 0;

    internal static void HideWindow(PhotinoWindow window)
    {
        ShowWindow(window.WindowHandle, Hide);
    }

    internal static void ShowWindow(PhotinoWindow window)
    {
        var handle = window.WindowHandle;
        ShowWindow(handle, Show);
        ShowWindow(handle, Restore);
        ShowContentWindows(handle);
        SetForegroundWindow(handle);
    }

    internal static void MinimizeWindow(PhotinoWindow window)
    {
        PostMessage(window.WindowHandle, WmSysCommand, (IntPtr)ScMinimize, IntPtr.Zero);
    }

    internal static void CloseWindow(PhotinoWindow window)
    {
        if (!PostMessage(window.WindowHandle, WmClose, IntPtr.Zero, IntPtr.Zero))
            throw new ApplicationException("Windows could not close the shell window.");
    }

    internal static bool IsVisible(PhotinoWindow window) => IsWindowVisible(window.WindowHandle);

    internal static bool IsMinimized(PhotinoWindow window) => IsIconic(window.WindowHandle);

    // IsWindowVisible stays true under a fullscreen app; the foreground window is the real signal.
    internal static bool IsForeground(PhotinoWindow window) => GetForegroundWindow() == window.WindowHandle;

    internal static bool IsCoveredByFullscreenWindow(PhotinoWindow window)
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == window.WindowHandle || IsDesktopWindow(foreground))
            return false;

        var monitor = MonitorFromWindow(foreground, MonitorDefaultToNull);
        if (monitor == IntPtr.Zero || monitor != MonitorFromWindow(window.WindowHandle, MonitorDefaultToNull))
            return false;

        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetWindowRect(foreground, out var bounds) || !GetMonitorInfo(monitor, ref monitorInfo))
            return false;

        return bounds.Left <= monitorInfo.Monitor.Left && bounds.Top <= monitorInfo.Monitor.Top
            && bounds.Right >= monitorInfo.Monitor.Right && bounds.Bottom >= monitorInfo.Monitor.Bottom;
    }

    private static bool IsDesktopWindow(IntPtr window)
    {
        if (window == GetShellWindow())
            return true;

        Span<char> name = stackalloc char[16];
        var length = GetClassName(window, ref MemoryMarshal.GetReference(name), name.Length);
        var className = name[..Math.Max(0, length)];
        return className.SequenceEqual("Progman") || className.SequenceEqual("WorkerW");
    }

    internal static void ShowContentWindows(PhotinoWindow window) => ShowContentWindows(window.WindowHandle);

    private static void ShowContentWindows(IntPtr windowHandle)
    {
        EnumChildWindows(windowHandle, static (childHandle, _) =>
        {
            ShowWindow(childHandle, Show);
            return true;
        }, IntPtr.Zero);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, ref char className, int maxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }
}
