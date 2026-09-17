// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Drawing;
using Photino.NET;
using Serilog;
using Tript.App;
using Tript.Settings;

namespace Tript.Shell;

internal sealed class ShellWindow : IDisposable
{
    private const string ReadyMessage = "tript:ready";

    private static readonly TimeSpan VisibilityPollInterval = TimeSpan.FromSeconds(1);

    private readonly string _url;
    private readonly AppHost _host;
    private readonly SingleInstance _singleInstance;
    private readonly string _iconPath;
    private WindowsTrayPresence? _tray;
    private WindowsHotkeys? _hotkeys;
    private Timer? _visibilityWatch;
    private PhotinoWindow? _window;
    private bool _activationPending;
    private bool _startupMinimizePending;
    private int _exitRequested;
    private bool _restartForUpdatePending;
    private bool _webReady;
    private string? _pendingNavigation;
    private StartupVisibility _startupVisibility;
    private bool _startupVisibilityApplied;

    internal ShellWindow(string url, AppHost host, SingleInstance singleInstance)
    {
        _url = url;
        _host = host;
        _singleInstance = singleInstance;
        _iconPath = Path.Combine(host.Options.WebRoot, "tript.ico");
    }

    internal bool Run()
    {
        _startupVisibility = _host.SettingsStore.Load().General.StartupVisibility;
        _tray = OperatingSystem.IsWindows() && File.Exists(_iconPath)
            ? new WindowsTrayPresence(
                _iconPath,
                () => _window is not null && WindowsWindow.IsVisible(_window),
                () => _host.IsRecording,
                HandleTrayCommand)
            : null;

        _hotkeys = OperatingSystem.IsWindows()
            ? new WindowsHotkeys(HandleHotkey, _host.PushError)
            : null;
        _hotkeys?.ApplyBindings(SettingsResolver.ResolveEffectiveHotkeys(_host.SettingsStore.Load()));
        _host.SettingsChanged += settings =>
            _window?.Invoke(() => _hotkeys?.ApplyBindings(SettingsResolver.ResolveEffectiveHotkeys(settings)));

        var window = CreateWindow();
        _window = window;

        _singleInstance.ActivationRequested += ShowMainWindow;
        _singleInstance.ExitRequested += RequestExit;
        _host.StateChanged += UpdateTrayState;
        _host.RestartForUpdateRequested += RestartForUpdate;
        _host.NotificationRequested += (kind, title, body) =>
            ShellNotifications.Show(_window, _host, kind, title, body);

        window.RegisterWindowClosingHandler((_, _) => OnClosing(window));
        AssignPickers(window);
        window.RegisterWindowCreatedHandler((_, _) => OnCreated(window));
        window.RegisterWebMessageReceivedHandler((_, message) => OnWebMessage(window, message));
        window.WindowMinimizedHandler = (_, _) => OnMinimized(window);

        if (_tray is not null && !_tray.Start())
        {
            Log.Warning(
                "Tript.Shell: the tray icon could not be registered; Tript will minimize to the taskbar instead of hiding");
        }

        _visibilityWatch = new Timer(_ => ReportVisibility(window), null,
            VisibilityPollInterval, VisibilityPollInterval);

        window.Load(Program.BuildLibraryUrl(_url));
        try
        {
            window.WaitForClose();
        }
        finally
        {
            _singleInstance.ActivationRequested -= ShowMainWindow;
            _singleInstance.ExitRequested -= RequestExit;
        }

        return _restartForUpdatePending;
    }

    public void Dispose()
    {
        _visibilityWatch?.Dispose();
        _hotkeys?.Dispose();
        _tray?.Dispose();
    }

    private PhotinoWindow CreateWindow()
    {
        var window = new PhotinoWindow
        {
            Title = "Tript",
            Size = new Size(1280, 800),
            FileSystemAccessEnabled = false,
            ContextMenuEnabled = true,
            DevToolsEnabled = false,
        };

        if (File.Exists(_iconPath))
            window.IconFile = _iconPath;
        if (OperatingSystem.IsWindows())
        {
            window.NotificationsEnabled = true;
            window.NotificationRegistrationId = WindowsAppIdentity.AppUserModelId;
            WindowsAppIdentity.EnsureStartMenuShortcut(
                ResolveLauncherExecutablePath(), File.Exists(_iconPath) ? _iconPath : null);
        }

        return window;
    }

    // SW_HIDE removes the taskbar button, so only hide once an icon is confirmed in the tray.
    private bool TrayReachable() => _tray is not null && _tray.EnsureIconPresent();

    private void HandleHotkey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.ToggleRecording:
                ThreadPool.QueueUserWorkItem(_ => _host.ToggleRecording());
                break;
            case HotkeyAction.ManualBookmark:
                ThreadPool.QueueUserWorkItem(_ => _host.AddLiveBookmark());
                break;
            case HotkeyAction.QuickClip:
                ThreadPool.QueueUserWorkItem(_ => _host.CreateQuickClipFromBuffer());
                break;
        }
    }

    private void ShowMainWindow()
    {
        var window = _window;
        if (window is null)
        {
            _activationPending = true;
            return;
        }

        try
        {
            if (!_startupVisibilityApplied)
                _startupVisibility = StartupVisibility.Window;
            window.Invoke(() => WindowsWindow.ShowWindow(window));
        }
        catch (ApplicationException)
        {
            _activationPending = true;
        }
    }

    private void RequestExit()
    {
        if (Interlocked.Exchange(ref _exitRequested, 1) != 0)
            return;

        var window = _window;
        if (window is null)
        {
            _host.Ipc.RequestShutdown();
            Environment.Exit(0);
            return;
        }

        // Off the message pump, which CloseWindow needs: stopping here flushes the session metadata.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (_host.IsRecording)
                _host.StopRecordingOrReport();

            Program.CloseShellOrRequestShutdown(
                () => WindowsWindow.CloseWindow(window),
                _host.Ipc.RequestShutdown,
                () => Environment.Exit(1));
        });
    }

    private void HandleTrayCommand(TrayCommand command)
    {
        if (command == TrayCommand.Exit)
        {
            RequestExit();
            return;
        }

        var window = _window;
        if (window is null)
            return;

        switch (command)
        {
            case TrayCommand.Show:
                ShowMainWindow();
                break;
            case TrayCommand.Hide:
                WindowsWindow.HideWindow(window);
                break;
            case TrayCommand.StartRecording:
                ThreadPool.QueueUserWorkItem(_ => _host.StartRecordingOrReport(null));
                break;
            case TrayCommand.StopRecording:
                ThreadPool.QueueUserWorkItem(_ => _host.StopRecordingOrReport());
                break;
            case TrayCommand.OpenSettings:
                ThreadPool.QueueUserWorkItem(_ => OpenSettings(window));
                break;
        }
    }

    private void OpenSettings(PhotinoWindow window)
    {
        try
        {
            window.Invoke(() =>
            {
                WindowsWindow.ShowWindow(window);
                if (_webReady)
                {
                    window.SendWebMessage(Program.NavigateSettingsMessage);
                }
                else
                {
                    _pendingNavigation = Program.NavigateSettingsMessage;
                }
            });
        }
        catch (ApplicationException)
        {
            _activationPending = true;
        }
    }

    private void UpdateTrayState(bool recording, string? gameId)
    {
        var tray = _tray;
        var window = _window;
        if (tray is null || window is null)
            return;

        try
        {
            window.Invoke(() => tray.SetRecordingState(recording, gameId));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Tript.Shell: could not update tray state: {exception.Message}");
        }
    }

    private void RestartForUpdate()
    {
        var window = _window;
        if (window is null)
            return;

        Interlocked.Exchange(ref _exitRequested, 1);
        _restartForUpdatePending = true;
        Program.CloseShellOrRequestShutdown(
            () => WindowsWindow.CloseWindow(window),
            _host.Ipc.RequestShutdown,
            () => Environment.Exit(ShellExitCodes.RestartForUpdate));
    }

    private bool OnClosing(PhotinoWindow window)
    {
        var decision = Program.DecideWindowClose(
            exitRequested: Volatile.Read(ref _exitRequested) != 0,
            hideToTray: _tray is not null
                && _host.SettingsStore.Load().General.CloseBehavior == CloseBehavior.HideToTray,
            trayReachable: TrayReachable,
            recording: _host.IsRecording);

        switch (decision)
        {
            case Program.WindowCloseDecision.HideToTray:
                WindowsWindow.HideWindow(window);
                return true;
            case Program.WindowCloseDecision.MinimizeToTaskbar:
                WindowsWindow.MinimizeWindow(window);
                return true;
            case Program.WindowCloseDecision.StopRecordingThenExit:
                RequestExit();
                return true;
            default:
                return false;
        }
    }

    private void AssignPickers(PhotinoWindow window)
    {
        _host.FolderPicker = () => NativePickers.PickRecordingFolder(window, _host);
        _host.TrainingFolderPicker = () => NativePickers.PickTrainingFolder(window);
        _host.GameExecutablePicker = () => NativePickers.PickExecutable(window);
    }

    private void OnCreated(PhotinoWindow window)
    {
        AssignPickers(window);
        _tray?.SetRecordingState(_host.IsRecording, _host.CurrentGameId);
        if (_activationPending)
        {
            _activationPending = false;
            _startupVisibility = StartupVisibility.Window;
            WindowsWindow.ShowWindow(window);
        }
    }

    private void ApplyStartupVisibility(PhotinoWindow window)
    {
        if (_startupVisibilityApplied)
            return;

        _startupVisibilityApplied = true;
        if (_startupVisibility == StartupVisibility.Tray && TrayReachable())
        {
            WindowsWindow.HideWindow(window);
        }
        else if (_startupVisibility != StartupVisibility.Window)
        {
            _startupMinimizePending = true;
            WindowsWindow.MinimizeWindow(window);
        }
    }

    private void OnWebMessage(PhotinoWindow window, string message)
    {
        if (!string.Equals(message, ReadyMessage, StringComparison.Ordinal))
            return;

        try
        {
            window.Invoke(() =>
            {
                _webReady = true;
                if (_pendingNavigation is not null)
                {
                    var navigation = _pendingNavigation;
                    _pendingNavigation = null;
                    window.SendWebMessage(navigation);
                }

                ApplyStartupVisibility(window);
            });
        }
        catch (ApplicationException)
        {
        }
    }

    private void OnMinimized(PhotinoWindow window)
    {
        if (_startupMinimizePending)
        {
            _startupMinimizePending = false;
            return;
        }

        if (_host.SettingsStore.Load().General.MinimizeBehavior == MinimizeBehavior.Tray
            && TrayReachable())
        {
            WindowsWindow.HideWindow(window);
        }
    }

    private void ReportVisibility(PhotinoWindow window)
    {
        try
        {
            if (window.WindowHandle == IntPtr.Zero)
                return;
            _host.SetWindowVisible(WindowsWindow.IsVisible(window) && !WindowsWindow.IsMinimized(window)
                && !WindowsWindow.IsCoveredByFullscreenWindow(window));
        }
        catch (Exception exception)
        {
            Log.Debug(exception, "Tript.Shell: could not read the window visibility");
        }
    }

    private static string ResolveLauncherExecutablePath()
    {
        var shellPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(shellPath))
            return string.Empty;

        var appDirectory = Path.GetDirectoryName(shellPath);
        var installRoot = appDirectory is null ? null : Path.GetDirectoryName(appDirectory);
        if (installRoot is null)
            return shellPath;

        var launcherPath = Path.Combine(installRoot, "Tript.exe");
        return File.Exists(launcherPath) ? launcherPath : shellPath;
    }
}
