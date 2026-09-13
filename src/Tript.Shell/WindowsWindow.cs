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

    // IsWindowVisible stays true for a restored window even when a fullscreen app fully covers it,
    // so it can't tell us whether the user is actually looking at Tript. The foreground window is
    // the real signal for "already looking at it".
    internal static bool IsForeground(PhotinoWindow window) => GetForegroundWindow() == window.WindowHandle;

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

    private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);
}
