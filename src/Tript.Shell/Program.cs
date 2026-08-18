// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Drawing;
using System.Net.Sockets;
using Photino.NET;
using Tript.App;

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
                if (!WaitForUi(UiAddress, TimeSpan.FromSeconds(15)))
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
                OpenWindow(host.UiUrl, host);
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
            var uri = new Uri(url);
            using var client = new TcpClient();
            client.Connect(uri.Host, uri.Port);
            return true;
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        {
            // Nothing listening yet, or the URI is unusable — both mean "not reachable", not a
            // shell failure. The bounded retry loop around this is what decides the outcome.
            return false;
        }
    }

    private static void OpenWindow(string url, AppHost host)
    {
        var window = new PhotinoWindow
        {
            Title = "Tript",
            Size = new Size(1280, 800),
            // The shell never lets the window's webview deal with file:// access: everything the
            // UI reads is served by the host's UiHost over HTTP.
            FileSystemAccessEnabled = false,
            ContextMenuEnabled = true,
            DevToolsEnabled = false,
        };

        // Allow the window to close immediately: the handler returns false so a close goes
        // through (true would prevent the window from closing and hang WaitForClose forever).
        // The handler is kept so a future windowed build can intercept close (unsaved state,
        // an in-flight recording); for now a close always goes through.
        window.RegisterWindowClosingHandler((_, _) => false);

        // Install the host's native folder picker once the window exists. Photino fires the
        // WindowCreated handler inside WaitForClose, after it has created the native window
        // (_nativeInstance is set), so the picker's ShowOpenFolder can marshal onto the GTK
        // thread safely when SetVideoLocation arrives on an IPC thread.
        window.RegisterWindowCreatedHandler((_, _) =>
        {
            host.FolderPicker = () => PickRecordingFolder(window, host);
        });

        window.Load(url);
        window.WaitForClose();
    }

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
