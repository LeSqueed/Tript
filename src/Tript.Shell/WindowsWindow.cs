// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.CompilerServices;
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
    private const uint ShowMaximized = 3;
    private const uint RestoreToMaximized = 0x0002;

    internal static void HideWindow(PhotinoWindow window)
    {
        ShowWindow(Handle(window), Hide);
    }

    // SW_RESTORE un-arranges as well as un-minimizes, so an unconditional restore drops a maximized
    // or snapped window back to its pre-arrange rect every time the tray activates it.
    internal static void ShowWindow(PhotinoWindow window)
    {
        var handle = Handle(window);
        ShowWindow(handle, IsIconic(handle) ? Restore : Show);
        ShowContentWindows(handle);
        SetForegroundWindow(handle);
    }

    internal static void MinimizeWindow(PhotinoWindow window)
    {
        PostMessage(Handle(window), WmSysCommand, (IntPtr)ScMinimize, IntPtr.Zero);
    }

    internal static void CloseWindow(PhotinoWindow window)
    {
        if (!PostMessage(Handle(window), WmClose, IntPtr.Zero, IntPtr.Zero))
            throw new ApplicationException("Windows could not close the shell window.");
    }

    private static readonly ConditionalWeakTable<PhotinoWindow, StrongBox<nint>> Handles = new();

    // Photino's WindowHandle getter goes through Photino_Invoke off the UI thread, and Photino.Native 4.0
    // wakes the waiting caller after it may have returned (use-after-free crash in RtlWakeConditionVariable).
    // The HWND never changes, so read it once, on the UI thread from OnCreated.
    internal static nint Handle(PhotinoWindow window)
    {
        if (Handles.TryGetValue(window, out var cached))
            return cached.Value;

        var handle = window.WindowHandle;
        if (handle != IntPtr.Zero)
            Handles.AddOrUpdate(window, new StrongBox<nint>(handle));
        return handle;
    }

    internal static WindowPlacement? TryGetPlacement(PhotinoWindow window)
    {
        var handle = Handle(window);
        var placement = new NativeWindowPlacement { Length = Marshal.SizeOf<NativeWindowPlacement>() };
        if (handle == IntPtr.Zero || !GetWindowPlacement(handle, ref placement))
            return null;

        var bounds = placement.NormalPosition;
        var maximized = IsZoomed(handle)
            || (IsIconic(handle) && (placement.Flags & RestoreToMaximized) != 0);
        return new WindowPlacement(bounds.Left, bounds.Top,
            bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, maximized);
    }

    internal static void ApplyPlacement(PhotinoWindow window, WindowPlacement saved)
    {
        var handle = Handle(window);
        var placement = new NativeWindowPlacement { Length = Marshal.SizeOf<NativeWindowPlacement>() };
        if (handle == IntPtr.Zero || !GetWindowPlacement(handle, ref placement))
            return;

        placement.NormalPosition = new NativeRect
        {
            Left = saved.Left,
            Top = saved.Top,
            Right = saved.Left + saved.Width,
            Bottom = saved.Top + saved.Height,
        };
        placement.Flags = 0;
        placement.ShowCommand = !IsWindowVisible(handle) ? (uint)Hide
            : saved.Maximized ? ShowMaximized
            : placement.ShowCommand;
        SetWindowPlacement(handle, ref placement);
    }

    internal static bool IsVisible(PhotinoWindow window) => IsWindowVisible(Handle(window));

    internal static bool IsMinimized(PhotinoWindow window) => IsIconic(Handle(window));

    // IsWindowVisible stays true under a fullscreen app; the foreground window is the real signal.
    internal static bool IsForeground(PhotinoWindow window) => GetForegroundWindow() == Handle(window);

    internal static bool IsCoveredByFullscreenWindow(PhotinoWindow window)
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == Handle(window) || IsDesktopWindow(foreground))
            return false;

        var monitor = MonitorFromWindow(foreground, MonitorDefaultToNull);
        if (monitor == IntPtr.Zero || monitor != MonitorFromWindow(Handle(window), MonitorDefaultToNull))
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

    internal static void ShowContentWindows(PhotinoWindow window) => ShowContentWindows(Handle(window));

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
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref NativeWindowPlacement placement);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref NativeWindowPlacement placement);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

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
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeWindowPlacement
    {
        public int Length;
        public uint Flags;
        public uint ShowCommand;
        public NativePoint MinPosition;
        public NativePoint MaxPosition;
        public NativeRect NormalPosition;
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
