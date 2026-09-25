// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Drawing;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Photino.NET;
using Serilog;
using Tript.App;
using Tript.Settings;
using Tript.Shell.Linux;

namespace Tript.Shell;

internal sealed class ShellWindow : IDisposable
{
    private const string ReadyMessage = "tript:ready";

    private static readonly TimeSpan VisibilityPollInterval = TimeSpan.FromSeconds(1);

    private readonly string _url;
    private readonly AppHost _host;
    private readonly SingleInstance _singleInstance;
    private readonly string _iconPath;
    private readonly WindowPlacementStore _placementStore = new();
    private readonly object _placementGate = new();
    private WindowPlacement? _savedPlacement;
    private WindowPlacement? _lastSeenPlacement;
    private volatile bool _placementTracked;
    private WindowsTrayPresence? _tray;
    private IGlobalHotkeys? _hotkeys;
    private LinuxShellNotifications? _linuxNotifications;
    private Timer? _visibilityWatch;
    private PhotinoWindow? _window;
    // These are written on the single-instance pipe thread, an AppHost background thread or a pool
    // thread and read on the UI thread, with no lock between them. Without volatile a stale read is
    // allowed; for _restartForUpdatePending that means exiting instead of restarting into a staged
    // update, silently.
    private volatile bool _activationPending;
    private bool _startupMinimizePending;
    private int _exitRequested;
    private volatile bool _restartForUpdatePending;
    private volatile bool _webReady;
    private volatile string? _pendingNavigation;
    private StartupVisibility _startupVisibility;
    private volatile bool _startupVisibilityApplied;
    private volatile bool _disposed;
    // Photino dereferences the native window inside Invoke, so a call made before OnCreated or after
    // the window closed is an access violation no catch can stop. The startup update check raising
    // its "update ready" notification did exactly that and killed the process.
    private volatile bool _nativeReady;
    private readonly IShellWindowControl _windowControl = ShellWindowControl.ForCurrentOs();
    private TerminationSignals? _terminationSignals;
    private volatile bool _hotkeysStale;
    private int _visibilityTicking;

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
                () => _window is not null && _windowControl.IsVisible(_window),
                () => _host.IsRecording,
                HandleTrayCommand)
            : null;

        if (OperatingSystem.IsWindows())
            SystemEvents.SessionEnding += OnSessionEnding;
        _terminationSignals = TerminationSignals.Register(() => StopRecordingThenExit("a termination signal"));

        _hotkeys = CreateHotkeys();
        _linuxNotifications = OperatingSystem.IsLinux() ? new LinuxShellNotifications(_host) : null;
        _hotkeys?.ApplyBindings(SettingsResolver.ResolveEffectiveHotkeys(_host.SettingsStore.Load()));
        _host.SettingsChanged += settings =>
        {
            if (!TryInvoke(() => _hotkeys?.ApplyBindings(SettingsResolver.ResolveEffectiveHotkeys(settings))))
                _hotkeysStale = true;
        };

        var window = CreateWindow();
        _window = window;

        _singleInstance.ActivationRequested += ShowMainWindow;
        _singleInstance.ExitRequested += RequestExit;
        _host.StatusChanged += UpdateTrayState;
        _host.RestartForUpdateRequested += RestartForUpdate;
        _host.NotificationRequested += (kind, title, body) =>
        {
            if (_linuxNotifications is { } linux)
                linux.Show(kind, title, body);
            else
                ShellNotifications.Show(_window, TryInvoke, _host, kind, title, body);
        };

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

        _visibilityWatch = new Timer(_ => OnVisibilityTick(window), null,
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
        _disposed = true;
        _nativeReady = false;
        if (OperatingSystem.IsWindows())
            SystemEvents.SessionEnding -= OnSessionEnding;

        // A plain Dispose returns while a tick may still be running against the window being torn
        // down. The wait is bounded because a tick can be parked in window.Invoke, which needs this
        // very thread, so waiting forever here would deadlock the exit.
        if (_visibilityWatch is { } watch)
        {
            using var settled = new ManualResetEvent(false);
            if (watch.Dispose(settled))
                settled.WaitOne(TimeSpan.FromSeconds(2));
        }
        _terminationSignals?.Dispose();
        _hotkeys?.Dispose();
        _linuxNotifications?.Dispose();
        _tray?.Dispose();
    }

    private IGlobalHotkeys? CreateHotkeys()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsHotkeys(HandleHotkey, _host.PushError);

        if (OperatingSystem.IsLinux())
        {
            var hotkeys = new LinuxHotkeys(HandleHotkey, _host.PushError,
                availability => _host.ReportGlobalHotkeys(availability.Available, availability.Note,
                    availability.ManagedByDesktop, availability.Configurable),
                _host.ReportDesktopHotkeyTriggers);
            _host.GlobalHotkeyConfigurator = hotkeys.ConfigureInDesktop;
            return hotkeys;
        }

        return null;
    }

    // Windows restart, shutdown and log off kill the process without any of the deliberate exit paths
    // running, so an in-progress recording loses its metadata and its mp4 is never finalized. This is
    // the only warning we get. Stop synchronously: the handler runs on the SystemEvents pump, not the
    // Photino message loop, and returning here is what tells Windows we are ready to go.
    [SupportedOSPlatform("windows")]
    private void OnSessionEnding(object? sender, SessionEndingEventArgs e) =>
        StopRecordingThenExit(e.Reason.ToString());

    private void StopRecordingThenExit(string reason)
    {
        try
        {
            if (_host.IsRecording)
                _host.StopRecordingOrReport();
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Tript.Shell: the recording could not be stopped for {Reason}", reason);
        }

        RequestExit();
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

        _savedPlacement = OperatingSystem.IsWindows()
            ? WindowPlacement.Sanitize(_placementStore.Load())
            : null;
        if (_savedPlacement is { } placement)
        {
            window.UseOsDefaultSize = false;
            window.UseOsDefaultLocation = false;
            window.Size = new Size(placement.Width, placement.Height);
            window.Location = new Point(placement.Left, placement.Top);
            window.Maximized = placement.Maximized;
        }

        var windowIcon = OperatingSystem.IsWindows()
            ? _iconPath
            : Path.Combine(_host.Options.WebRoot, "tript.png");
        if (File.Exists(windowIcon))
            window.IconFile = windowIcon;
        if (_linuxNotifications is { } linux)
        {
            window.RegisterFocusInHandler((_, _) => linux.SetWindowFocused(true));
            window.RegisterFocusOutHandler((_, _) => linux.SetWindowFocused(false));
        }
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
                RunInBackground(_host.ToggleRecording, "toggle recording");
                break;
            case HotkeyAction.ManualBookmark:
                RunInBackground(_host.AddLiveBookmark, "add a bookmark");
                break;
            case HotkeyAction.QuickClip:
                RunInBackground(_host.CreateQuickClipFromBuffer, "save a quick clip");
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
            if (!TryInvoke(() => _windowControl.Show(window)))
                _activationPending = true;
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
            // A throw here used to skip the close below and crash the pool thread, so the exit never
            // completed. A failed stop is logged and the window still closes.
            try
            {
                if (_host.IsRecording)
                    _host.StopRecordingOrReport();
            }
            catch (Exception exception)
            {
                Log.Error(exception, "Tript.Shell: the recording could not be stopped before exit");
            }

            Program.CloseShellOrRequestShutdown(
                () => _windowControl.Close(window),
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
                if (!_nativeReady)
                    break;
                SavePlacement(window);
                _windowControl.Hide(window);
                break;
            case TrayCommand.StartRecording:
                RunInBackground(() => _host.StartRecordingOrReport(null), "start recording");
                break;
            case TrayCommand.StopRecording:
                RunInBackground(_host.StopRecordingOrReport, "stop recording");
                break;
            case TrayCommand.OpenSettings:
                RunInBackground(() => OpenSettings(window), "open settings");
                break;
        }
    }

    private void OpenSettings(PhotinoWindow window)
    {
        try
        {
            var invoked = TryInvoke(() =>
            {
                _windowControl.Show(window);
                if (_webReady)
                {
                    window.SendWebMessage(Program.NavigateSettingsMessage);
                }
                else
                {
                    _pendingNavigation = Program.NavigateSettingsMessage;
                }
            });
            if (!invoked)
            {
                _pendingNavigation = Program.NavigateSettingsMessage;
                _activationPending = true;
            }
        }
        catch (ApplicationException)
        {
            _activationPending = true;
        }
    }

    private void UpdateTrayState(TrayStatus status)
    {
        var tray = _tray;
        var window = _window;
        if (tray is null || window is null)
            return;

        try
        {
            TryInvoke(() => tray.SetStatus(status));
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
            () => _windowControl.Close(window),
            _host.Ipc.RequestShutdown,
            () => Environment.Exit(ShellExitCodes.RestartForUpdate));
    }

    private bool OnClosing(PhotinoWindow window)
    {
        SavePlacement(window);
        var decision = Program.DecideWindowClose(
            exitRequested: Volatile.Read(ref _exitRequested) != 0,
            hideToTray: _tray is not null
                && _host.SettingsStore.Load().General.CloseBehavior == CloseBehavior.HideToTray,
            trayReachable: TrayReachable,
            recording: _host.IsRecording);

        switch (decision)
        {
            case Program.WindowCloseDecision.HideToTray:
                _windowControl.Hide(window);
                return true;
            case Program.WindowCloseDecision.MinimizeToTaskbar:
                _windowControl.Minimize(window);
                return true;
            case Program.WindowCloseDecision.StopRecordingThenExit:
                RequestExit();
                return true;
            default:
                _nativeReady = false;
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
        _nativeReady = true;
        if (_hotkeysStale)
        {
            _hotkeysStale = false;
            _hotkeys?.ApplyBindings(SettingsResolver.ResolveEffectiveHotkeys(_host.SettingsStore.Load()));
        }
        AssignPickers(window);
        TrackPlacement(window);
        _tray?.SetStatus(_host.CurrentTrayStatus());
        if (_activationPending)
        {
            _activationPending = false;
            _startupVisibility = StartupVisibility.Window;
            _windowControl.Show(window);
        }
    }

    private bool TryInvoke(Action action)
    {
        var window = _window;
        if (window is null || !_nativeReady || _disposed)
            return false;

        window.Invoke(action);
        return true;
    }

    private void ApplyStartupVisibility(PhotinoWindow window)
    {
        if (_startupVisibilityApplied)
            return;

        _startupVisibilityApplied = true;
        if (_startupVisibility == StartupVisibility.Tray && TrayReachable())
        {
            _windowControl.Hide(window);
        }
        else if (_startupVisibility != StartupVisibility.Window)
        {
            _startupMinimizePending = true;
            _windowControl.Minimize(window);
        }
    }

    internal const string ClientErrorPrefix = "tript:client-error:";
    private const int MaxClientErrorLength = 8 * 1024;
    private const int MaxClientErrorReports = 200;
    private int _clientErrorReports;

    // The UI's error boundary and global handlers report here over the native bridge, which still
    // works when the control socket is the thing that broke. Bounded again on this side: the page
    // already truncates, but a UI stuck in a render loop must not be able to fill the disk.
    private void LogClientError(string payload)
    {
        if (Interlocked.Increment(ref _clientErrorReports) > MaxClientErrorReports)
            return;

        if (payload.Length > MaxClientErrorLength)
            payload = payload[..MaxClientErrorLength];

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(payload);
            var root = document.RootElement;
            var kind = root.TryGetProperty("kind", out var k) ? k.GetString() : null;
            var text = root.TryGetProperty("message", out var m) ? m.GetString() : null;
            var stack = root.TryGetProperty("stack", out var s) ? s.GetString() : null;
            Log.Error("UI {Kind} error: {Message}{Stack}", kind ?? "unknown", text ?? "(no message)",
                string.IsNullOrEmpty(stack) ? string.Empty : Environment.NewLine + stack);
        }
        catch (System.Text.Json.JsonException)
        {
            Log.Error("UI error (unparseable report): {Payload}", payload);
        }
    }

    private void OnWebMessage(PhotinoWindow window, string message)
    {
        if (message.StartsWith(ClientErrorPrefix, StringComparison.Ordinal))
        {
            LogClientError(message[ClientErrorPrefix.Length..]);
            return;
        }

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

        SavePlacement(window);

        if (_host.SettingsStore.Load().General.MinimizeBehavior == MinimizeBehavior.Tray
            && TrayReachable())
        {
            _windowControl.Hide(window);
        }
    }

    // An exception escaping a pool thread terminates the process, so a failed hotkey or tray action
    // used to take a running recording down with it.
    private static void RunInBackground(Action action, string what)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                Log.Error(exception, "Tript.Shell: could not {What}", what);
            }
        });
    }

    // ReportVisibility blocks in window.Invoke, which needs the UI thread. While a native folder
    // picker holds that thread (up to five minutes) the one-second timer used to queue a new blocked
    // callback every tick, around three hundred of them, starving the pool that hotkeys, the tray and
    // the clip pipeline all run on. Skipping a tick while one is still running bounds that to one.
    private void OnVisibilityTick(PhotinoWindow window)
    {
        if (_disposed || Interlocked.Exchange(ref _visibilityTicking, 1) != 0)
            return;

        try
        {
            if (!_disposed)
                ReportVisibility(window);
        }
        catch (Exception exception)
        {
            Log.Debug(exception, "Tript.Shell: a window visibility check failed");
        }
        finally
        {
            Volatile.Write(ref _visibilityTicking, 0);
        }
    }

    private void ReportVisibility(PhotinoWindow window)
    {
        try
        {
            if (OperatingSystem.IsWindows() && window.WindowHandle == IntPtr.Zero)
                return;
            _host.SetWindowVisible(_windowControl.IsSeenByTheUser(window));
        }
        catch (Exception exception)
        {
            Log.Debug(exception, "Tript.Shell: could not read the window visibility");
        }

        SaveSettledPlacement(window);
    }

    private void TrackPlacement(PhotinoWindow window)
    {
        if (!OperatingSystem.IsWindows())
            return;

        if (_savedPlacement is { } saved)
            WindowsWindow.ApplyPlacement(window, saved);
        _placementTracked = true;
    }

    private void SaveSettledPlacement(PhotinoWindow window)
    {
        if (!_placementTracked || Volatile.Read(ref _exitRequested) != 0)
            return;

        try
        {
            WindowPlacement? current = null;
            window.Invoke(() => current = WindowsWindow.TryGetPlacement(window));
            if (current is null)
                return;

            var settled = current == _lastSeenPlacement;
            _lastSeenPlacement = current;
            if (settled)
                SavePlacementIfChanged(current);
        }
        catch (Exception exception)
        {
            Log.Debug(exception, "Tript.Shell: could not read the window placement");
        }
    }

    private void SavePlacement(PhotinoWindow window)
    {
        if (!_placementTracked)
            return;

        try
        {
            if (WindowsWindow.TryGetPlacement(window) is { } current)
                SavePlacementIfChanged(current);
        }
        catch (Exception exception)
        {
            Log.Debug(exception, "Tript.Shell: could not read the window placement");
        }
    }

    private void SavePlacementIfChanged(WindowPlacement current)
    {
        lock (_placementGate)
        {
            if (current == _savedPlacement)
                return;

            _placementStore.Save(current);
            _savedPlacement = current;
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
