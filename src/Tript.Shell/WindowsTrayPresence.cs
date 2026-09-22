// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Tript.App;

namespace Tript.Shell;

internal enum TrayCommand
{
    Show,
    Hide,
    StartRecording,
    StopRecording,
    OpenSettings,
    Exit,
}

internal sealed class WindowsTrayPresence : IDisposable
{
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifGuid = 0x00000020;
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;
    private const uint NotifyIconVersion4 = 4;
    private const uint WmApp = 0x8000;
    private const uint WmTray = WmApp + 1;
    private const uint WmCommand = 0x0111;
    private const uint WmContextMenu = 0x007B;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDoubleClick = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmSettingChange = 0x001A;
    private const uint WmUser = 0x0400;
    private const uint NinSelect = WmUser;
    private const uint NinKeySelect = WmUser + 1;
    private const uint MfString = 0x0000;
    private const uint MfSeparator = 0x0800;
    private const uint MfByCommand = 0x0000;
    private const uint MfEnabled = 0x0000;
    private const uint MfGrayed = 0x0001;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCommand = 0x0100;
    private const uint WsExToolWindow = 0x00000080;

    private static readonly Guid IconGuid = new("7d3e3b6a-8e5c-4dd6-9d89-ea98f5bde2d4");
    private static readonly string WindowClassName = $"TriptTray_{IconGuid:N}";

    private readonly Action<TrayCommand> _command;
    private readonly Func<bool> _isWindowVisible;
    private readonly Func<bool> _isRecording;
    private readonly string _iconPath;
    private readonly WndProcDelegate _wndProc;
    private readonly uint _taskbarCreated;
    private readonly uint _trayCallbackMessage = WmTray;
    private readonly Dictionary<(TrayActivity, TrayAlert), IntPtr> _icons = new();
    private IntPtr _messageWindow;
    private IntPtr _icon;
    private int _iconSize = 16;
    private bool _added;
    private bool _useGuid = true;
    private bool _disposed;
    private TrayStatus _status = TrayStatus.Idle;
    private string _tooltip = "Tript";

    internal WindowsTrayPresence(string iconPath, Func<bool> isWindowVisible, Func<bool> isRecording,
        Action<TrayCommand> command)
    {
        _iconPath = iconPath;
        _isWindowVisible = isWindowVisible;
        _isRecording = isRecording;
        _command = command;
        _wndProc = WndProc;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    }

    internal bool Start()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            _messageWindow = CreateMessageWindow();
            _iconSize = TrayIconFactory.TrayIconSize();
            _icon = IconFor(_status);
            if (_icon == IntPtr.Zero)
                throw new InvalidOperationException($"Could not load the tray icon '{_iconPath}'.");

            AddIcon();
            return true;
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine($"Tript.Shell: the tray icon is unavailable: {exception.Message}");
            return false;
        }
    }

    // Hiding without a confirmed tray icon leaves no window, taskbar button or icon. The retry
    // mutates icon state, so callers must be on the message window's STA thread.
    internal bool EnsureIconPresent()
    {
        if (_disposed || !OperatingSystem.IsWindows())
            return false;

        if (_added)
            return true;

        if (_messageWindow == IntPtr.Zero || _icon == IntPtr.Zero)
            return false;

        try
        {
            AddIcon();
        }
        catch (InvalidOperationException)
        {
        }

        return _added;
    }

    internal void SetStatus(TrayStatus status)
    {
        if (_disposed || !OperatingSystem.IsWindows())
            return;

        _status = status;
        _tooltip = status.Tooltip();
        _icon = IconFor(status);
        // NIF_ICON with a null handle blanks the tray entry, so never modify without a drawn icon.
        if (_icon != IntPtr.Zero && EnsureIconPresent())
            ModifyIcon();
    }

    [SupportedOSPlatform("windows")]
    private IntPtr IconFor(TrayStatus status)
    {
        var key = (status.Activity, status.Alert);
        if (_icons.TryGetValue(key, out var cached))
            return cached;

        IntPtr icon;
        try
        {
            icon = TrayIconFactory.Create(_iconPath, _iconSize, status.Activity, status.Alert);
        }
        catch (Exception exception) when (exception is ArgumentException or SystemException)
        {
            Console.Error.WriteLine($"Tript.Shell: the tray icon could not be drawn: {exception.Message}");
            return _icon;
        }

        _icons[key] = icon;
        return icon;
    }

    // A DPI or taskbar-size change alters SM_CXSMICON, so cached icons become the wrong size.
    [SupportedOSPlatform("windows")]
    private void RebuildIcons()
    {
        var size = TrayIconFactory.TrayIconSize();
        if (size == _iconSize)
            return;

        _iconSize = size;
        DestroyIcons();
        _icon = IconFor(_status);
        if (_icon != IntPtr.Zero && EnsureIconPresent())
            ModifyIcon();
    }

    private void DestroyIcons()
    {
        foreach (var icon in _icons.Values)
            DestroyIcon(icon);
        _icons.Clear();
        _icon = IntPtr.Zero;
    }

    private IntPtr CreateMessageWindow()
    {
        var module = GetModuleHandle(null);
        var windowClass = new WndClass
        {
            Style = 0,
            WindowProcedure = Marshal.GetFunctionPointerForDelegate(_wndProc),
            Instance = module,
            ClassName = WindowClassName,
        };
        RegisterClass(ref windowClass);

        var window = CreateWindowEx(
            WsExToolWindow,
            WindowClassName,
            "Tript tray",
            0,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            module,
            IntPtr.Zero);
        if (window == IntPtr.Zero)
            throw new InvalidOperationException("Windows could not create the Tript tray message window.");

        return window;
    }

    // A GUID icon is bound to the exe path that first registered it, so a moved install falls
    // back to hwnd + id. Never NIM_DELETE the shared GUID: it would remove another instance's icon.
    private void AddIcon()
    {
        _useGuid = true;
        var data = BuildNotifyIconData(NifMessage | NifIcon | NifTip | NifGuid);
        if (!ShellNotifyIcon(NimAdd, ref data))
        {
            _useGuid = false;
            data = BuildNotifyIconData(NifMessage | NifIcon | NifTip);
            if (!ShellNotifyIcon(NimAdd, ref data))
                throw new InvalidOperationException("Windows could not add the Tript tray icon.");
        }

        data.Version = NotifyIconVersion4;
        ShellNotifyIcon(NimSetVersion, ref data);
        _added = true;
    }

    private void ModifyIcon()
    {
        var flags = NifTip | NifIcon;
        var data = BuildNotifyIconData(_useGuid ? flags | NifGuid : flags);
        ShellNotifyIcon(NimModify, ref data);
    }

    private NotifyIconData BuildNotifyIconData(uint flags) => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = _messageWindow,
        Id = 1,
        Flags = flags,
        CallbackMessage = _trayCallbackMessage,
        Icon = _icon,
        Tip = _tooltip,
        Guid = _useGuid ? IconGuid : Guid.Empty,
    };

    private IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == _taskbarCreated)
        {
            if (!_disposed)
            {
                DeleteIcon();
                EnsureIconPresent();
            }
            return IntPtr.Zero;
        }

        if (message == WmSettingChange)
        {
            if (!_disposed && OperatingSystem.IsWindows())
                RebuildIcons();
            return DefWindowProc(window, message, wParam, lParam);
        }

        if (message == _trayCallbackMessage)
        {
            var notification = unchecked((uint)lParam.ToInt64()) & 0xffff;
            if (notification is WmLButtonUp or WmLButtonDoubleClick or NinSelect or NinKeySelect)
                _command(TrayCommand.Show);
            else if (notification is WmRButtonUp or WmContextMenu)
                ShowMenu();
            return IntPtr.Zero;
        }

        if (message == WmCommand)
        {
            var command = unchecked((uint)wParam.ToInt64()) & 0xffff;
            var visible = _isWindowVisible();
            _command(command switch
            {
                1 => MenuVisibilityCommand(visible),
                2 => TrayCommand.StartRecording,
                3 => TrayCommand.StopRecording,
                4 => TrayCommand.OpenSettings,
                5 => TrayCommand.Exit,
                _ => TrayCommand.Show,
            });
            return IntPtr.Zero;
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;

        if (_status.Alert != TrayAlert.None && !string.IsNullOrWhiteSpace(_status.AlertReason))
        {
            AppendMenu(menu, MfGrayed, 0, _status.AlertReason);
            AppendMenu(menu, MfSeparator, 0, null);
        }

        var visible = _isWindowVisible();
        AppendMenu(menu, MfString, 1, visible ? "Hide Tript" : "Show Tript");
        AppendMenu(menu, MfSeparator, 0, null);
        AppendMenu(menu, _isRecording() ? MfGrayed : MfEnabled, 2, "Start recording");
        AppendMenu(menu, _isRecording() ? MfEnabled : MfGrayed, 3, "Stop recording");
        AppendMenu(menu, MfString, 4, "Open Settings");
        AppendMenu(menu, MfSeparator, 0, null);
        AppendMenu(menu, MfString, 5, "Exit");

        GetCursorPos(out var point);
        SetForegroundWindow(_messageWindow);
        var command = TrackPopupMenu(menu, TpmRightButton | TpmReturnCommand,
            point.X, point.Y, 0, _messageWindow, IntPtr.Zero);
        DestroyMenu(menu);
        PostMessage(_messageWindow, 0, IntPtr.Zero, IntPtr.Zero);
        if (command != 0)
            SendMessage(_messageWindow, WmCommand, (IntPtr)command, IntPtr.Zero);
    }

    internal static TrayCommand MenuVisibilityCommand(bool visible) =>
        visible ? TrayCommand.Hide : TrayCommand.Show;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        DeleteIcon();
        DestroyIcons();

        if (_messageWindow != IntPtr.Zero)
        {
            DestroyWindow(_messageWindow);
            _messageWindow = IntPtr.Zero;
        }
    }

    private void DeleteIcon()
    {
        if (!_added)
            return;

        var data = BuildNotifyIconData(_useGuid ? NifGuid : 0);
        ShellNotifyIcon(NimDelete, ref data);
        _added = false;
        _useGuid = true;
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        internal uint Size;
        internal IntPtr Window;
        internal uint Id;
        internal uint Flags;
        internal uint CallbackMessage;
        internal IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Tip;
        internal uint State;
        internal uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Info;
        internal uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string InfoTitle;
        internal uint InfoFlags;
        internal Guid Guid;
        internal IntPtr BalloonIcon;
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

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string? text);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved,
        IntPtr window, IntPtr rectangle);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
