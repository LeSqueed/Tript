// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

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

                using var shell = new ShellWindow(host.UiUrl, host, singleInstance);
                restartForUpdate = shell.Run();
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
        catch (ApplicationException)
        {
            requestShutdown();
            forceExit();
        }
    }
}
