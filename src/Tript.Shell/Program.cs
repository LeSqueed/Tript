// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Drawing;
using Photino.NET;
using Tript.App;
using Tript.Settings;

namespace Tript.Shell;

// The desktop shell: the same host as the headless launcher (Tript.App's Program.BuildApp seam —
// src/Tript.App/Program.cs) behind a native window instead of a browser tab. The app's React UI is
// served over HTTP by the host's UiHost (http://localhost:2882/) and rendered in a Photino webview,
// which keeps the UI and the protocol exactly the headless build already uses.
//
// Lifetime, split across two threads because both sides demand the main thread:
//   * AppHost.Run() blocks in WaitForShutdown until Ipc.RequestShutdown() fires, so it runs on a
//     background thread — READY/SHUTDOWN still print exactly as in the headless build.
//   * PhotinoWindow.WaitForClose() runs the GTK/WebKit event loop and must be on the main thread,
//     so the window is opened here.
// Closing the window requests shutdown, which unblocks the background Run(), and the host is then
// disposed on the main thread after the background thread has drained.
internal static class Program
{
    // Where the UI host listens. The URL the window actually loads carries the launch's session
    // token (host.UiUrl) and is built in-process — never a command-line argument, and never in the
    // message below.
    private static readonly string UiAddress = $"http://localhost:{LocalPorts.Ui}/";
    private static readonly HttpClient UiClient = new()
    {
        Timeout = TimeSpan.FromSeconds(2),
    };

    // STAThread on the entry point: WebView2's CoreWebView2Controller must be created on a
    // single-threaded apartment (the controller holds COM state the apartment owns). The .NET
    // runtime initialises the main thread as MTA by default, and without this the Photino webview
    // opens as a black window — the browser processes start, the page is reachable, nothing renders.
    // libobs runs on its own dedicated STA thread (Tript.App.Program.StartRuntimeOnHostThread), so
    // the two apartments stay separate.
    [STAThread]
    private static int Main(string[] args)
    {
        var options = AppOptions.Parse(args);
        if (options is null)
            return 2;

        using var singleInstance = SingleInstance.TryAcquire();
        if (singleInstance is null)
            return 0;

        // BuildApp starts the libobs runtime (or not, with --fake-recorder) and wires the host, so
        // a missing OBS runtime is surfaced here with the same exit contract as the headless
        // launcher: a message on stderr and exit 1.
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

            try
            {
                // Wait until the host is serving before pointing the webview at the UI, so the
                // first paint is not a connection failure. Give up gracefully: a webview that
                // cannot reach the host is a hang, and a hang is the one failure the shell must
                // never have — report it on stderr and exit non-zero.
                if (!WaitForUi(host.UiUrl, TimeSpan.FromSeconds(15)))
                {
                    Console.Error.WriteLine(
                        "Tript.Shell: the app host did not come up in time (no reply from " + UiAddress +
                        "); giving up.");
                    return 1;
                }

                // Same principle, the other hang: on Linux the webview's WebKitGTK render process
                // aborts when GStreamer has no audio sink, which shows up as a window that opens
                // and appears frozen while this host stays perfectly healthy. The check is a no-op
                // on Windows (WebView2) and fails open whenever it cannot tell — see
                // WebviewAudioSink for the measured detail and why absence is only ever reported
                // on positive evidence.
                if (!WebviewAudioSink.IsPresent())
                {
                    Console.Error.WriteLine(WebviewAudioSink.MissingSinkMessage);
                    return 1;
                }

                // WaitForClose runs the Photino/GTK event loop and returns when the window closes.
                // The window is the whole shell UI, so the process exits when it is gone. The URL
                // is the tokenised one, handed over in memory: the UI host serves nothing without
                // it, and the document request trades it for a cookie so the assets follow.
                OpenWindow(host.UiUrl, host, singleInstance);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Tript.Shell: {exception}");
                return 1;
            }
            finally
            {
                // The window is gone (closed or failed); unblock the host's WaitForShutdown and
                // wait for the background thread to drain before disposing the host.
                host.Ipc.RequestShutdown();
                hostThread.Join();
            }
        }

        return 0;
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
            // Startup registration is a preference, not a reason to prevent recording. Keep the
            // setting persisted and report the platform failure for the user to act on.
            Console.Error.WriteLine($"Tript.Shell: could not update Windows startup registration: {exception.Message}");
        }
    }

    private static void RunHost(AppHost host)
    {
        try
        {
            host.Run();
        }
        catch (Exception exception)
        {
            // A host that fails during startup (a bind conflict, a refused runtime) is reported on
            // stderr here; the shell's WaitForUi gives up after its timeout and the process exits
            // non-zero. The host never throws after it reaches WaitForShutdown.
            Console.Error.WriteLine($"Tript.Shell: the app host failed: {exception}");
        }
    }

    // Polls the UI host until it answers. The host prints READY after starting its servers, serves
    // the UI over HTTP and blocks in WaitForShutdown; by the time READY has been observed the UI
    // host is accepting connections.
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
            // Nothing listening yet, the URI is unusable, or the request timed out — all mean "not
            // reachable", not a shell failure. The bounded retry loop decides the outcome.
            return false;
        }
    }

    private static void OpenWindow(string url, AppHost host, SingleInstance singleInstance)
    {
            var general = host.SettingsStore.Load().General;
            var iconPath = Path.Combine(host.Options.WebRoot, "tript.ico");
        var startupUrl = BuildLibraryUrl(url);
        PhotinoWindow? window = null;
        var activationPending = false;
        var startupMinimizePending = false;
        var exitRequested = false;
        var startupVisibility = general.StartupVisibility;
        var startupVisibilityApplied = false;
        using var tray = OperatingSystem.IsWindows() && File.Exists(iconPath)
            ? new WindowsTrayPresence(
                iconPath,
                () => window is not null && WindowsWindow.IsVisible(window),
                () => host.IsRecording,
                command => HandleTrayCommand(command))
            : null;

        window = new PhotinoWindow
        {
            Title = "Tript",
            Size = new Size(1280, 800),
            // The shell never lets the window's webview deal with file:// access: everything the
            // UI reads is served by the host's UiHost over HTTP.
            FileSystemAccessEnabled = false,
            ContextMenuEnabled = true,
            DevToolsEnabled = false,
        };

        if (File.Exists(iconPath))
            window.IconFile = iconPath;
        if (OperatingSystem.IsWindows())
        {
            // Keep the native channel enabled for the shell lifetime. The persisted preference is
            // checked by the notification sink, while Photino does not allow this property to change
            // after the native window has been created.
            window.NotificationsEnabled = true;
            window.NotificationRegistrationId = "Tript";
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
                                // Use a fresh fragment so WebView2 does not coalesce repeated tray
                                // requests into a no-op when the document is already on Settings.
                                window.Load(BuildSettingsUrl(url));
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
                    if (host.IsRecording)
                        host.StopRecordingOrReport();

                    try
                    {
                        window.Invoke(() => WindowsWindow.CloseWindow(window));
                    }
                    catch (ApplicationException)
                    {
                        activationPending = true;
                    }
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
        host.NotificationRequested += (kind, title, body) =>
        {
            if (!OperatingSystem.IsWindows() || window is null)
                return;

            var notifications = host.SettingsStore.Load().General.Notifications;
            if (!notifications.Enabled || !NotificationEnabled(notifications, kind))
                return;

            try
            {
                window.Invoke(() =>
                {
                    if (WindowsWindow.IsVisible(window) && !window.Minimized)
                        return;
                    window.SendNotification(title, body);
                });
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Tript.Shell: could not send notification: {exception.Message}");
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

        // Install the host's native folder picker once the window exists. Photino fires the
        // WindowCreated handler inside WaitForClose, after it has created the native window
        // (_nativeInstance is set), so the picker's ShowOpenFolder can marshal onto the GTK
        // thread safely when SetVideoLocation arrives on an IPC thread.
        window.RegisterWindowCreatedHandler((_, _) =>
        {
            host.FolderPicker = () => PickRecordingFolder(window, host);
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
                window.Invoke(ApplyStartupVisibility);
            }
            catch (ApplicationException)
            {
                // The page-ready message can race window teardown; WaitForClose will perform the
                // normal cleanup and there is no visibility action left to apply.
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
    }

    internal static string BuildLibraryUrl(string url) => $"{url}#library";

    internal static string BuildSettingsUrl(string url) => $"{url}#settings-{Guid.NewGuid():N}";

    internal static bool NotificationEnabled(NotificationSettings settings, NotificationKind kind) => kind switch
    {
        NotificationKind.RecordingStarted => settings.RecordingStarted,
        NotificationKind.RecordingStopped => settings.RecordingStopped,
        NotificationKind.Error => settings.Errors,
        NotificationKind.Recovery => settings.Recovery,
        _ => false,
    };

    // Runs the native "select a folder" dialog and returns the chosen directory, or null when the
    // user cancels. SetVideoLocation arrives on the host's IPC receive thread, so the dialog must
    // be marshalled onto the window's UI thread: Invoke dispatches the workItem to the GTK main
    // thread (gdk_threads_add_idle) and blocks until it returns.
    private static string? PickRecordingFolder(PhotinoWindow window, AppHost host)
    {
        // Start the dialog at the current setting so the user sees where recordings go today;
        // fall back to the platform default when the field is empty.
        var configured = host.SettingsStore.Load().Recording.OutputDirectory;
        var defaultPath = string.IsNullOrWhiteSpace(configured)
            ? Tript.Settings.RecordingLocations.DefaultDirectory()
            : configured;

        string? result = null;
        window.Invoke(() =>
        {
            var picked = window.ShowOpenFolder("Select recordings folder", defaultPath, multiSelect: false);
            if (picked.Length > 0)
                result = picked[0];
        });
        return result;
    }
}
