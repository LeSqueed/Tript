// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Photino.NET;

namespace Tript.Shell;

internal interface IShellWindowControl
{
    void Show(PhotinoWindow window);

    void Hide(PhotinoWindow window);

    void Minimize(PhotinoWindow window);

    void Close(PhotinoWindow window);

    bool IsVisible(PhotinoWindow window);

    bool IsSeenByTheUser(PhotinoWindow window);
}

internal static class ShellWindowControl
{
    internal static IShellWindowControl ForCurrentOs() =>
        OperatingSystem.IsWindows() ? new WindowsShellWindowControl() : new PhotinoShellWindowControl();
}

internal sealed class WindowsShellWindowControl : IShellWindowControl
{
    public void Show(PhotinoWindow window) => WindowsWindow.ShowWindow(window);

    public void Hide(PhotinoWindow window) => WindowsWindow.HideWindow(window);

    public void Minimize(PhotinoWindow window) => WindowsWindow.MinimizeWindow(window);

    public void Close(PhotinoWindow window) => WindowsWindow.CloseWindow(window);

    public bool IsVisible(PhotinoWindow window) => WindowsWindow.IsVisible(window);

    public bool IsSeenByTheUser(PhotinoWindow window) =>
        WindowsWindow.IsVisible(window)
        && !WindowsWindow.IsMinimized(window)
        && !WindowsWindow.IsCoveredByFullscreenWindow(window);
}

internal sealed class PhotinoShellWindowControl : IShellWindowControl
{
    public void Show(PhotinoWindow window) => window.Minimized = false;

    public void Hide(PhotinoWindow window) => window.Minimized = true;

    public void Minimize(PhotinoWindow window) => window.Minimized = true;

    public void Close(PhotinoWindow window) => window.Invoke(window.Close);

    public bool IsVisible(PhotinoWindow window) => true;

    public bool IsSeenByTheUser(PhotinoWindow window)
    {
        var minimized = false;
        window.Invoke(() => minimized = window.Minimized);
        return !minimized;
    }
}
