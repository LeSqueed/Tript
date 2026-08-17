// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;

namespace Tript.Shell;

// The Linux webview's audio-sink preflight, and the only reason it exists is a measured platform
// fact: WebKitGTK builds a GStreamer media pipeline for its render process at startup, and when
// GStreamer cannot supply an audio sink that pipeline cannot be completed and the render process
// aborts. Verified on this machine — WebKitWebProcess takes SIGABRT with every frame inside
// libwebkit2gtk-4.1.so and not one frame in our code, preceded on stdout by:
//
//     GStreamer element autoaudiosink not found. Please install it
//     (WebKitWebProcess:NNNN): GLib-GObject-CRITICAL **: g_signal_connect_data:
//         assertion 'G_TYPE_CHECK_INSTANCE (instance)' failed
//
// autoaudiosink lives in GStreamer's "good" plugin set (Arch/CachyOS gst-plugins-good,
// Debian/Ubuntu gstreamer1.0-plugins-good), which is not a hard dependency of webkit2gtk-4.1, so a
// machine can be missing it with everything else the shell needs installed. Nothing in the Tript UI
// plays audio, but WebKitGTK constructs the pipeline regardless, so this hits *every* user without
// that package — see the Linux system dependencies in README.md.
//
// What makes it worth a preflight is the symptom: the window opens and appears frozen while the
// .NET host is perfectly healthy — still serving the UI over HTTP on 2882, still holding its three
// ports, libobs fine — so nothing on screen points at a missing GStreamer plugin. That is exactly
// the failure Program's WaitForUi check exists to rule out ("a hang is the one failure the shell
// must never have"), so the shell answers the question before opening the window and reports the
// package to install instead.
//
// The check is deliberately lopsided. A false "missing" would refuse to start on a working install,
// which is worse than the freeze it prevents, so absence is only ever reported when something
// positively said the element is not there; every unknown (an unfamiliar plugin layout, no
// gst-inspect-1.0 on PATH, a timeout, any error at all) counts as present and startup continues.
internal static class WebviewAudioSink
{
    // The element WebKitGTK asks GStreamer for, and the plugin that registers it. The plugin is
    // probed as a file because that costs a handful of stat calls, where building GStreamer's
    // registry costs a process launch.
    internal const string Element = "autoaudiosink";
    internal const string PluginLibrary = "libgstautodetect.so";
    internal const string InspectProgram = "gst-inspect-1.0";

    // Bounded so the check cannot become the hang it prevents. A cold registry rebuild is the slow
    // case and still lands well inside this; anything slower is treated as unknown (fail open).
    private static readonly TimeSpan InspectTimeout = TimeSpan.FromSeconds(3);

    // GStreamer's own plugin search: these environment variables first (they are how a vendored or
    // relocated GStreamer — a flatpak runtime, a self-built stack — is found at all), then the
    // system directories. Values hold several directories separated by ':' on Unix.
    private static readonly string[] PluginPathVariables =
    [
        "GST_PLUGIN_PATH_1_0",
        "GST_PLUGIN_PATH",
        "GST_PLUGIN_SYSTEM_PATH_1_0",
        "GST_PLUGIN_SYSTEM_PATH",
    ];

    // The distro layouts: Arch and Fedora (lib / lib64), Debian and Ubuntu's multiarch directory,
    // /usr/local for hand-built installs, and the per-user plugin directory.
    private static readonly string[] SystemPluginDirectories =
    [
        "/usr/lib/gstreamer-1.0",
        "/usr/lib64/gstreamer-1.0",
        "/usr/lib/x86_64-linux-gnu/gstreamer-1.0",
        "/usr/local/lib/gstreamer-1.0",
        "/usr/local/lib64/gstreamer-1.0",
        "/usr/local/lib/x86_64-linux-gnu/gstreamer-1.0",
    ];

    // What the shell prints when the element is genuinely absent. It names the element, the reason
    // the shell refuses to open the window, and the package per distro, because the whole point of
    // the guard is that the user can act on the message without knowing anything about WebKitGTK.
    internal static string MissingSinkMessage =>
        $"Tript.Shell: GStreamer has no \"{Element}\" element, so the desktop shell cannot start." +
        Environment.NewLine +
        "  WebKitGTK (the webview) builds a GStreamer audio pipeline at startup and aborts its" +
        Environment.NewLine +
        "  render process without an audio sink: the window would open and sit there frozen while" +
        Environment.NewLine +
        "  the app host keeps running. The Tript UI plays no audio — WebKitGTK needs the sink" +
        Environment.NewLine +
        "  regardless. Install GStreamer's \"good\" plugin set and start Tript again:" +
        Environment.NewLine +
        "    Arch / CachyOS:  sudo pacman -S gst-plugins-good" +
        Environment.NewLine +
        "    Debian / Ubuntu: sudo apt install gstreamer1.0-plugins-good" +
        Environment.NewLine +
        "  Or run the headless host instead (Tript.App), which needs no webview at all.";

    // The check as the shell runs it: the real environment, the real filesystem, and a bounded
    // gst-inspect-1.0 as the tie-breaker.
    internal static bool IsPresent() =>
        IsPresent(
            OperatingSystem.IsWindows(),
            Environment.GetEnvironmentVariable,
            File.Exists,
            () => Inspect(InspectProgram, Element, InspectTimeout));

    // The decision, pure: every fact it uses comes in through a probe, so the outcome is assertable
    // without depending on what happens to be installed on the machine running the tests.
    //
    // Order is cheapest-first, and only a positive "not there" is allowed to fail the check:
    //   * Windows renders with WebView2 — GStreamer is not in the picture, so "present".
    //   * the plugin's shared object in a plugin directory — present, and no process is launched.
    //   * otherwise gst-inspect-1.0, which reads the registry this machine actually uses: false
    //     means the element really is missing, null means we could not tell and startup continues.
    internal static bool IsPresent(
        bool isWindows,
        Func<string, string?> environment,
        Func<string, bool> fileExists,
        Func<bool?> inspectElement)
    {
        if (isWindows)
            return true;

        foreach (var candidate in CandidatePluginLibraries(environment))
        {
            if (fileExists(candidate))
                return true;
        }

        // Fail open on anything short of a definite "no such element".
        return inspectElement() ?? true;
    }

    // Every path where libgstautodetect.so could plausibly live, in search order: the environment's
    // plugin directories first, then the system ones, de-duplicated so a variable that repeats a
    // system directory does not cost a second stat.
    internal static IReadOnlyList<string> CandidatePluginLibraries(Func<string, string?> environment)
    {
        var directories = new List<string>();

        foreach (var variable in PluginPathVariables)
        {
            var value = environment(variable);
            if (string.IsNullOrWhiteSpace(value))
                continue;

            // ':' rather than Path.PathSeparator: this is GLib's search-path separator on Unix, and
            // this list is only ever consulted on Unix.
            directories.AddRange(value.Split(':',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        directories.AddRange(SystemPluginDirectories);

        return directories
            .Distinct(StringComparer.Ordinal)
            .Select(directory => Path.Combine(directory, PluginLibrary))
            .ToList();
    }

    // Asks gst-inspect-1.0 about an element: exit 0 means it exists, a non-zero exit means it does
    // not ("No such element or plugin"), and anything else — the program is not installed, it
    // outstays the timeout, the launch fails — is null, "could not tell".
    //
    // The child is killed when it outstays the timeout, so this can never be the thing that hangs
    // startup, and its output is redirected and drained (BeginOutputReadLine) so a chatty element
    // dump neither reaches the user's terminal nor fills a pipe buffer and deadlocks the wait.
    internal static bool? Inspect(string program, string element, TimeSpan timeout)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(program, element)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
                return null;

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                TryKill(process);
                return null;
            }

            return process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // No gst-inspect-1.0 on PATH is the common case here (Win32Exception), but every
            // failure to ask means the same thing: unknown, so let the shell start.
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException
                                              or System.ComponentModel.Win32Exception)
        {
            // It exited on its own between the wait and the kill, or the platform refused: either
            // way the answer is already "unknown" and there is nothing left to do.
        }
    }
}
