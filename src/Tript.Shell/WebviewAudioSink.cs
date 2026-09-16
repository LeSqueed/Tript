// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;

namespace Tript.Shell;

internal static class WebviewAudioSink
{
    internal const string Element = "autoaudiosink";
    internal const string PluginLibrary = "libgstautodetect.so";
    internal const string InspectProgram = "gst-inspect-1.0";

    private static readonly TimeSpan InspectTimeout = TimeSpan.FromSeconds(3);

    private static readonly string[] PluginPathVariables =
    [
        "GST_PLUGIN_PATH_1_0",
        "GST_PLUGIN_PATH",
        "GST_PLUGIN_SYSTEM_PATH_1_0",
        "GST_PLUGIN_SYSTEM_PATH",
    ];

    private static readonly string[] SystemPluginDirectories =
    [
        "/usr/lib/gstreamer-1.0",
        "/usr/lib64/gstreamer-1.0",
        "/usr/lib/x86_64-linux-gnu/gstreamer-1.0",
        "/usr/local/lib/gstreamer-1.0",
        "/usr/local/lib64/gstreamer-1.0",
        "/usr/local/lib/x86_64-linux-gnu/gstreamer-1.0",
    ];

    internal static string MissingSinkMessage =>
        $"Tript.Shell: GStreamer has no \"{Element}\" element, so the desktop shell cannot start." +
        Environment.NewLine +
        "  WebKitGTK (the webview) builds a GStreamer audio pipeline at startup and aborts its" +
        Environment.NewLine +
        "  render process without an audio sink: the window would open and sit there frozen while" +
        Environment.NewLine +
        "  the app host keeps running. The Tript UI plays no audio; WebKitGTK needs the sink" +
        Environment.NewLine +
        "  regardless. Install GStreamer's \"good\" plugin set and start Tript again:" +
        Environment.NewLine +
        "    Arch / CachyOS:  sudo pacman -S gst-plugins-good" +
        Environment.NewLine +
        "    Debian / Ubuntu: sudo apt install gstreamer1.0-plugins-good" +
        Environment.NewLine +
        "  Or run the headless host instead (Tript.App), which needs no webview at all.";

    internal static bool IsPresent() =>
        IsPresent(
            OperatingSystem.IsWindows(),
            Environment.GetEnvironmentVariable,
            File.Exists,
            () => Inspect(InspectProgram, Element, InspectTimeout));

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

        return inspectElement() ?? true;
    }

    internal static IReadOnlyList<string> CandidatePluginLibraries(Func<string, string?> environment)
    {
        var directories = new List<string>();

        foreach (var variable in PluginPathVariables)
        {
            var value = environment(variable);
            if (string.IsNullOrWhiteSpace(value))
                continue;

            directories.AddRange(value.Split(':',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        directories.AddRange(SystemPluginDirectories);

        return directories
            .Distinct(StringComparer.Ordinal)

            .Select(directory => directory.TrimEnd('/') + "/" + PluginLibrary)
            .ToList();
    }

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
        }
    }
}
