// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;
using Tript.Settings;

namespace Tript.Shell;

// Global (OS-level) hotkeys, mirroring WindowsTrayPresence's shape: a hidden window receives Win32
// messages and a constructor-injected callback turns them into app commands. Uses a true
// message-only window (HWND_MESSAGE) rather than the tray's tool window, since this one is never
// shown and never owns a popup. Its WM_HOTKEY messages are pumped by the same per-thread message
// loop Photino's window already relies on (this window is created on the same STA thread, before
// PhotinoWindow.WaitForClose blocks pumping it) — no dedicated thread needed, same as the tray.
internal sealed class WindowsHotkeys : IDisposable
{
    private const uint WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private static readonly IntPtr HwndMessage = new(-3);

    private static readonly string WindowClassName = $"TriptHotkeys_{Guid.NewGuid():N}";

    private readonly Action<HotkeyAction> _onHotkey;
    private readonly Action<string> _onRegistrationFailed;
    private readonly WndProcDelegate _wndProc;
    private readonly Dictionary<int, HotkeyAction> _registered = new();
    private IntPtr _messageWindow;
    private bool _disposed;

    internal WindowsHotkeys(Action<HotkeyAction> onHotkey, Action<string> onRegistrationFailed)
    {
        _onHotkey = onHotkey;
        _onRegistrationFailed = onRegistrationFailed;
        _wndProc = WndProc;
        _messageWindow = CreateMessageWindow();
    }

    internal void ApplyBindings(IReadOnlyDictionary<HotkeyAction, HotkeyBinding?> effective)
    {
        foreach (var id in _registered.Keys)
            UnregisterHotKey(_messageWindow, id);
        _registered.Clear();

        foreach (var (action, binding) in effective)
        {
            if (binding is null || string.IsNullOrEmpty(binding.Key))
                continue;

            if (!HotkeyBindingFormat.TryParse(binding, out var modifiers, out var virtualKey))
                continue;

            var id = (int)action + 1;
            if (!RegisterHotKey(_messageWindow, id, modifiers | ModNoRepeat, virtualKey))
            {
                _onRegistrationFailed(
                    $"Could not register the {action} hotkey. Another application may already be using it.");
                continue;
            }

            _registered[id] = action;
        }
    }

    private IntPtr CreateMessageWindow()
    {
        var module = GetModuleHandle(null);
        var windowClass = new WndClass
        {
            WindowProcedure = Marshal.GetFunctionPointerForDelegate(_wndProc),
            Instance = module,
            ClassName = WindowClassName,
        };
        RegisterClass(ref windowClass);

        var window = CreateWindowEx(0, WindowClassName, "Tript hotkeys", 0, 0, 0, 0, 0,
            HwndMessage, IntPtr.Zero, module, IntPtr.Zero);
        if (window == IntPtr.Zero)
            throw new InvalidOperationException("Windows could not create the Tript hotkey message window.");

        return window;
    }

    private IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmHotkey && _registered.TryGetValue((int)wParam.ToInt64(), out var action))
        {
            _onHotkey(action);
            return IntPtr.Zero;
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        foreach (var id in _registered.Keys)
            UnregisterHotKey(_messageWindow, id);
        _registered.Clear();

        if (_messageWindow != IntPtr.Zero)
        {
            DestroyWindow(_messageWindow);
            _messageWindow = IntPtr.Zero;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProcDelegate(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        internal uint Size;
        internal uint Style;
        internal IntPtr WindowProcedure;
        internal int ClassExtra;
        internal int WindowExtra;
        internal IntPtr Instance;
        internal IntPtr Icon;
        internal IntPtr Cursor;
        internal IntPtr Background;
        internal string? MenuName;
        internal string ClassName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WndClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
