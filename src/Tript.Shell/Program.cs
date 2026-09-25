// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;
using Serilog;
using Tript.App;

namespace Tript.Shell;

internal static class Program
{
    internal const string NavigateSettingsMessage = "tript:navigate:settings";
    internal const string ExitArgument = "--exit";

    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(90);

    private static readonly string UiAddress = $"http://localhost:{LocalPorts.Ui}/";
    private static readonly HttpClient UiClient = new()
    {
        Timeout = TimeSpan.FromSeconds(2),
    };

    [STAThread]
    private static int Main(string[] args)
    {
        if (IsExitRequest(args))
            return RequestExitOfRunningInstance();

        var options = AppOptions.Parse(args);
        if (options is null)
            return 2;

        AppLog.InstallCrashHandlers();
        try
        {
            return Run(options);
        }
        finally
        {
            AppLog.Shutdown();
        }
    }

    private static int Run(AppOptions options)
    {
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
            Log.Fatal(exception, "Tript.Shell: the app host could not be built");
            ReportStartupFailure("Tript could not start.", exception.Message);
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
                    // host.UiUrl carries the session token, which must never reach a log file.
                    Log.Fatal("Tript.Shell: the app host did not come up in time (no reply on port {Port}); giving up",
                        LocalPorts.Ui);
                    ReportStartupFailure("Tript could not start because its interface did not come up in time.", null);
                    return 1;
                }

                if (!WebviewAudioSink.IsPresent())
                {
                    Log.Fatal("Tript.Shell: {Message}", WebviewAudioSink.MissingSinkMessage);
                    ReportStartupFailure("Tript could not start.", WebviewAudioSink.MissingSinkMessage);
                    return 1;
                }

                using var shell = new ShellWindow(host.UiUrl, host, singleInstance);
                restartForUpdate = shell.Run();
            }
            catch (Exception exception)
            {
                Log.Fatal(exception, "Tript.Shell: the window failed");
                ReportStartupFailure("Tript stopped unexpectedly.", exception.Message);
                return 1;
            }
            finally
            {
                host.Ipc.RequestShutdown();
                if (!hostThread.Join(TimeSpan.FromSeconds(5)))
                {
                    // Environment.Exit skips every finally and Dispose below, including the one that
                    // would flush the log, so flush first or the reason for the kill is lost too.
                    Log.Fatal("Tript.Shell: the app host did not stop; terminating the process");
                    AppLog.Shutdown();
                    Environment.Exit(1);
                }
            }
            return restartForUpdate ? ShellExitCodes.RestartForUpdate : 0;
        }
    }

    internal static bool IsExitRequest(string[] args) =>
        args.Any(argument => string.Equals(argument, ExitArgument, StringComparison.Ordinal));

    private static int RequestExitOfRunningInstance() =>
        SingleInstance.RequestExit(ExitWait) ? 0 : ShellExitCodes.ExitRequestTimedOut;

    private static void ApplyStartupRegistration(WindowsStartupRegistration registration, bool enabled)
    {
        try
        {
            registration.Apply(enabled, Environment.ProcessPath
                ?? throw new InvalidOperationException("The shell executable path is unavailable."));
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Tript.Shell: could not update Windows startup registration");
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
            Log.Fatal(exception, "Tript.Shell: the app host failed");
        }
    }

    // Tript.Shell is a WinExe, and a Linux desktop launch has no terminal either, so there is nothing
    // the user can see when startup fails: launching Tript just does nothing. This is the only
    // feedback on that path.
    private static void ReportStartupFailure(string summary, string? detail)
    {
        const int maxDetail = 400;
        if (detail is { Length: > maxDetail })
            detail = detail[..maxDetail] + "...";

        var logLocation = AppLog.CurrentFile ?? AppLog.LogDirectory;
        var paragraphs = string.IsNullOrWhiteSpace(detail)
            ? new[] { summary, "Details were written to:" + Environment.NewLine + logLocation }
            : new[] { summary, detail, "Details were written to:" + Environment.NewLine + logLocation };
        var text = string.Join(Environment.NewLine + Environment.NewLine, paragraphs);

        if (!OperatingSystem.IsWindows())
        {
            StartupFailureDialog.Show(text);
            return;
        }

        try
        {
            MessageBoxW(IntPtr.Zero, text, "Tript", MbOk | MbIconError | MbSetForeground);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    private const uint MbOk = 0x00000000;
    private const uint MbIconError = 0x00000010;
    private const uint MbSetForeground = 0x00010000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);

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

    internal static string BuildLibraryUrl(string url) => $"{url}#library";

    internal enum WindowCloseDecision
    {
        Close,
        HideToTray,
        MinimizeToTaskbar,
        StopRecordingThenExit,
    }

    internal static WindowCloseDecision DecideWindowClose(
        bool exitRequested, bool hideToTray, Func<bool> trayReachable, bool recording)
    {
        if (exitRequested)
            return WindowCloseDecision.Close;
        if (hideToTray)
            return trayReachable() ? WindowCloseDecision.HideToTray : WindowCloseDecision.MinimizeToTaskbar;
        return recording ? WindowCloseDecision.StopRecordingThenExit : WindowCloseDecision.Close;
    }

    internal static void CloseShellOrRequestShutdown(
        Action closeShell, Action requestShutdown, Action forceExit)
    {
        try
        {
            closeShell();
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Tript.Shell: the window could not be closed; shutting the host down instead");
            requestShutdown();
            forceExit();
        }
    }
}
