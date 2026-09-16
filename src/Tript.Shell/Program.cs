// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Drawing;
using Photino.NET;
using Serilog;
using Tript.App;
using Tript.Settings;

namespace Tript.Shell;

internal static class Program
{
    internal const string NavigateSettingsMessage = "tript:navigate:settings";

    private static readonly string UiAddress = $"http://localhost:{LocalPorts.Ui}/";
    private static readonly HttpClient UiClient = new()
    {
        Timeout = TimeSpan.FromSeconds(2),
    };

    [STAThread]
    private static int Main(string[] args)
    {
        var options = AppOptions.Parse(args);
        if (options is null)
            return 2;

        using var singleInstance = SingleInstance.TryAcquire();
        if (singleInstance is null)
            return 0;

        AppHost host;
        try
        {
            host = Tript.App.Program.BuildApp(options);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Tript.Shell: {exception}");
            return 1;
        }

        using (host)
        {
            var startupRegistration = WindowsStartupRegistration.Create();
            ApplyStartupRegistration(startupRegistration, host.SettingsStore.Load().General.StartWithWindows);
            host.SettingsChanged += settings =>
                ApplyStartupRegistration(startupRegistration, settings.General.StartWithWindows);

            var hostThread = new Thread(() => RunHost(host))
            {
                IsBackground = true,
                Name = "Tript.Shell.Host",
            };
            hostThread.Start();

            var restartForUpdate = false;
            try
            {
                if (!WaitForUi(host.UiUrl, TimeSpan.FromSeconds(15)))
                {
                    Console.Error.WriteLine(
                        "Tript.Shell: the app host did not come up in time (no reply from " + host.UiUrl +
                        "); giving up.");
                    return 1;
                }

                if (!WebviewAudioSink.IsPresent())
                {
                    Console.Error.WriteLine(WebviewAudioSink.MissingSinkMessage);
                    return 1;
                }

                restartForUpdate = OpenWindow(host.UiUrl, host, singleInstance);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Tript.Shell: {exception}");
                return 1;
            }
            finally
            {
                host.Ipc.RequestShutdown();
                if (!hostThread.Join(TimeSpan.FromSeconds(5)))
                {
                    Console.Error.WriteLine("Tript.Shell: the app host did not stop; terminating the process.");
                    Environment.Exit(1);
                }
            }
            return restartForUpdate ? ShellExitCodes.RestartForUpdate : 0;
        }
    }

    private static void ApplyStartupRegistration(WindowsStartupRegistration registration, bool enabled)
    {
        try
        {
            registration.Apply(enabled, Environment.ProcessPath
                ?? throw new InvalidOperationException("The shell executable path is unavailable."));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Tript.Shell: could not update Windows startup registration: {exception.Message}");
        }
    }

    // The Start Menu shortcut has to point at what the user actually launches - the native
    // launcher one level above App\ - not at this process's own Tript.Shell.exe. Falls back to the
    // shell itself when there's no launcher beside it (a dev run straight out of App\).
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

    private static void RunHost(AppHost host)
    {
        try
        {
            host.Run();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Tript.Shell: the app host failed: {exception}");
        }
    }

    private static bool WaitForUi(string url, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (UiReachable(url))
                return true;

            Thread.Sleep(200);
        }

        return false;
    }

    private static bool UiReachable(string url)
    {
        try
        {
            using var response = UiClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                .GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or ArgumentException)
        {
            return false;
        }
    }

    private static bool OpenWindow(string url, AppHost host, SingleInstance singleInstance)
    {
            var general = host.SettingsStore.Load().General;
            var iconPath = Path.Combine(host.Options.WebRoot, "tript.ico");
        var startupUrl = BuildLibraryUrl(url);
        PhotinoWindow? window = null;
        var activationPending = false;
        var startupMinimizePending = false;
        var exitRequested = false;
        var restartForUpdatePending = false;
        var webReady = false;
        string? pendingNavigation = null;
        var startupVisibility = general.StartupVisibility;
        var startupVisibilityApplied = false;
        using var tray = OperatingSystem.IsWindows() && File.Exists(iconPath)
            ? new WindowsTrayPresence(
                iconPath,
                () => window is not null && WindowsWindow.IsVisible(window),
                () => host.IsRecording,
                command => HandleTrayCommand(command))
            : null;

        using var hotkeys = OperatingSystem.IsWindows()
            ? new WindowsHotkeys(action => HandleHotkey(action), host.PushError)
            : null;
        hotkeys?.ApplyBindings(SettingsResolver.ResolveEffectiveHotkeys(host.SettingsStore.Load()));
        host.SettingsChanged += settings =>
            window?.Invoke(() => hotkeys?.ApplyBindings(SettingsResolver.ResolveEffectiveHotkeys(settings)));

        void HandleHotkey(HotkeyAction action)
        {
            switch (action)
            {
                case HotkeyAction.ToggleRecording:
                    ThreadPool.QueueUserWorkItem(_ => host.ToggleRecording());
                    break;
                case HotkeyAction.ManualBookmark:
                    ThreadPool.QueueUserWorkItem(_ => host.AddLiveBookmark());
                    break;
                case HotkeyAction.QuickClip:
                    ThreadPool.QueueUserWorkItem(_ => host.CreateQuickClipFromBuffer());
                    break;
            }
        }

        window = new PhotinoWindow
        {
            Title = "Tript",
            Size = new Size(1280, 800),

            FileSystemAccessEnabled = false,
            ContextMenuEnabled = true,
            DevToolsEnabled = false,
        };

        if (File.Exists(iconPath))
            window.IconFile = iconPath;
        if (OperatingSystem.IsWindows())
        {
            window.NotificationsEnabled = true;
            window.NotificationRegistrationId = WindowsAppIdentity.AppUserModelId;
            WindowsAppIdentity.EnsureStartMenuShortcut(
                ResolveLauncherExecutablePath(), File.Exists(iconPath) ? iconPath : null);
        }

        void ShowMainWindow()
        {
            if (window is null)
            {
                activationPending = true;
                return;
            }

            try
            {
                if (!startupVisibilityApplied)
                    startupVisibility = StartupVisibility.Window;
                window.Invoke(() => WindowsWindow.ShowWindow(window));
            }
            catch (ApplicationException)
            {
                activationPending = true;
            }
        }

        singleInstance.ActivationRequested += ShowMainWindow;

        void HandleTrayCommand(TrayCommand command)
        {
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
                    ThreadPool.QueueUserWorkItem(_ => host.StartRecordingOrReport(null));
                    break;
                case TrayCommand.StopRecording:
                    ThreadPool.QueueUserWorkItem(_ => host.StopRecordingOrReport());
                    break;
                case TrayCommand.OpenSettings:
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            window.Invoke(() =>
                            {
                                WindowsWindow.ShowWindow(window);
                                if (webReady)
                                {
                                    window.SendWebMessage(NavigateSettingsMessage);
                                }
                                else
                                {
                                    pendingNavigation = NavigateSettingsMessage;
                                }
                            });
                        }
                        catch (ApplicationException)
                        {
                            activationPending = true;
                        }
                    });
                    break;
                case TrayCommand.Exit:
                    exitRequested = true;
                    CloseShellOrRequestShutdown(
                        () => window.Invoke(() => WindowsWindow.CloseWindow(window)),
                        host.Ipc.RequestShutdown,
                        () => Environment.Exit(1));
                    break;
            }
        }

        void UpdateTrayState(bool recording, string? gameId)
        {
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

        host.StateChanged += UpdateTrayState;
        host.RestartForUpdateRequested += () =>
        {
            if (window is null)
                return;

            exitRequested = true;
            restartForUpdatePending = true;
            CloseShellOrRequestShutdown(
                () => window.Invoke(() => WindowsWindow.CloseWindow(window)),
                host.Ipc.RequestShutdown,
                () => Environment.Exit(ShellExitCodes.RestartForUpdate));
        };
        host.NotificationRequested += (kind, title, body) =>
        {
            if (!OperatingSystem.IsWindows() || window is null)
                return;

            var notifications = host.SettingsStore.Load().General.Notifications;
            if (!notifications.Enabled)
                return;

            var showToast = NotificationEnabled(notifications, kind);
            var playSound = SoundEnabled(notifications, kind);
            if (!showToast && !playSound)
                return;

            try
            {
                window.Invoke(() =>
                {
                    if (WindowsWindow.IsForeground(window))
                        return;
#if WINDOWS_TOAST
                    if (showToast)
                        WindowsToastNotifications.Show(title, body, Path.Combine(host.Options.WebRoot, "tript.png"));
                    if (playSound)
                        NativeSound.Play(kind, host.Options.WebRoot);
#else
                    if (showToast)
                        window.SendNotification(title, body);
#endif
                });
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "Tript.Shell: could not send notification");
            }
        };

        window.RegisterWindowClosingHandler((_, _) =>
        {
            if (exitRequested)
                return false;

            if (tray is null)
                return false;

            var settings = host.SettingsStore.Load().General;
            if (host.IsRecording)
                host.StopRecordingOrReport();

            var shouldHide = settings.CloseBehavior == CloseBehavior.HideToTray;
            if (!shouldHide)
                return false;

            WindowsWindow.HideWindow(window);
            return true;
        });

        host.FolderPicker = () => PickRecordingFolder(window, host);
        host.TrainingFolderPicker = () => PickTrainingFolder(window);
        host.GameExecutablePicker = () => PickExecutable(window);

        window.RegisterWindowCreatedHandler((_, _) =>
        {
            host.FolderPicker = () => PickRecordingFolder(window, host);
            host.TrainingFolderPicker = () => PickTrainingFolder(window);
            host.GameExecutablePicker = () => PickExecutable(window);
            tray?.SetRecordingState(host.IsRecording, host.CurrentGameId);
            if (activationPending)
            {
                activationPending = false;
                startupVisibility = StartupVisibility.Window;
                WindowsWindow.ShowWindow(window);
            }
        });

        void ApplyStartupVisibility()
        {
            if (startupVisibilityApplied)
                return;

            startupVisibilityApplied = true;
            if (startupVisibility == StartupVisibility.Minimized)
            {
                startupMinimizePending = true;
                WindowsWindow.MinimizeWindow(window);
            }
            else if (startupVisibility == StartupVisibility.Tray)
            {
                WindowsWindow.HideWindow(window);
            }
        }

        window.RegisterWebMessageReceivedHandler((_, message) =>
        {
            if (!string.Equals(message, "tript:ready", StringComparison.Ordinal))
                return;

            try
            {
                window.Invoke(() =>
                {
                    webReady = true;
                    if (pendingNavigation is not null)
                    {
                        var navigation = pendingNavigation;
                        pendingNavigation = null;
                        window.SendWebMessage(navigation);
                    }

                    ApplyStartupVisibility();
                });
            }
            catch (ApplicationException)
            {
            }
        });

        window.WindowMinimizedHandler = (_, _) =>
        {
            if (startupMinimizePending)
            {
                startupMinimizePending = false;
                return;
            }

            if (tray is not null && host.SettingsStore.Load().General.MinimizeBehavior == MinimizeBehavior.Tray)
                WindowsWindow.HideWindow(window);
        };

        tray?.Start();
        window.Load(startupUrl);
        try
        {
            window.WaitForClose();
        }
        finally
        {
            singleInstance.ActivationRequested -= ShowMainWindow;
        }

        return restartForUpdatePending;
    }

    internal static string BuildLibraryUrl(string url) => $"{url}#library";

    internal static void CloseShellOrRequestShutdown(
        Action closeShell, Action requestShutdown, Action forceExit)
    {
        try
        {
            closeShell();
        }
        catch (ApplicationException)
        {
            requestShutdown();
            forceExit();
        }
    }

    internal static bool NotificationEnabled(NotificationSettings settings, NotificationKind kind) => kind switch
    {
        NotificationKind.RecordingStarted => settings.RecordingStarted,
        NotificationKind.RecordingStopped => settings.RecordingStopped,
        NotificationKind.Error => settings.Errors,
        NotificationKind.UpdateReady => true,
        _ => false,
    };

    internal static bool SoundEnabled(NotificationSettings settings, NotificationKind kind) => kind switch
    {
        NotificationKind.RecordingStarted => settings.RecordingStartedSound,
        NotificationKind.RecordingStopped => settings.RecordingStoppedSound,
        NotificationKind.Error => settings.ErrorsSound,
        NotificationKind.UpdateReady => false,
        _ => false,
    };

    private static string? PickRecordingFolder(PhotinoWindow window, AppHost host)
    {
        var configured = host.SettingsStore.Load().Recording.OutputDirectory;
        var defaultPath = string.IsNullOrWhiteSpace(configured)
            ? Tript.Settings.RecordingLocations.DefaultDirectory()
            : configured;

        return PickFolder(window, "Select recordings folder", ExistingFolderOrParent(defaultPath));
    }

    private static string? PickTrainingFolder(PhotinoWindow window)
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
            documents = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return PickFolder(window, "Select training data folder",
            ExistingFolderOrParent(Path.Combine(documents, "Tript", "training")));
    }

    private static string ExistingFolderOrParent(string path)
    {
        var candidate = Path.GetFullPath(path);
        while (!Directory.Exists(candidate))
        {
            var parent = Directory.GetParent(candidate)?.FullName;
            if (parent is null || string.Equals(parent, candidate, StringComparison.OrdinalIgnoreCase))
                return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidate = parent;
        }
        return candidate;
    }

    private static string? PickFolder(PhotinoWindow window, string title, string defaultPath)
    {
        string? result = null;
        using var completed = new ManualResetEventSlim(false);
        window.Invoke(() =>
        {
            try
            {
                var picked = window.ShowOpenFolder(title, defaultPath, multiSelect: false);
                if (picked.Length > 0)
                    result = picked[0];
            }
            finally
            {
                completed.Set();
            }
        });

        if (!completed.Wait(TimeSpan.FromMinutes(5)))
            throw new TimeoutException("The native folder picker did not return.");
        return result;
    }

    private static string? PickExecutable(PhotinoWindow window)
    {
        string? result = null;
        using var completed = new ManualResetEventSlim(false);
        window.Invoke(() =>
        {
            try
            {
                var picked = window.ShowOpenFile("Select game executable",
                    ExistingFolderOrParent(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
                    multiSelect: false);
                if (picked.Length > 0)
                    result = picked[0];
            }
            finally
            {
                completed.Set();
            }
        });

        if (!completed.Wait(TimeSpan.FromMinutes(5)))
            throw new TimeoutException("The native executable picker did not return.");
        return result;
    }
}
