// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;

namespace Tript.Obs.IntegrationTests;

// Drives the Tript.RecorderHarness executable as a child process. The harness exists because the
// ffmpeg_muxer plugin spawns its obs-ffmpeg-mux helper next to the *actual binary* of the process
// (os_get_executable_path_ptr resolves /proc/self/exe, measured), and under `dotnet test` the
// process that would start the output is dotnet itself, which cannot have a helper dropped beside
// it. The harness's apphost is a real executable whose directory carries a copy of the helper, so a
// recording can actually run from under the test host.
//
// The harness's contract is a single `RESULT:` line on stdout (plus libobs's own log lines, which
// the harness writes to stdout). Run returns that line's verdict; the parent asserts on the file on
// disk and on the probe.
internal sealed class ObsRecorderHarnessDriver
{
    private const string HarnessFileName = "Tript.RecorderHarness";
    private const string HelperFileName = ObsMuxerHelper.HelperFileName;

    private static readonly string HarnessDirectory =
        Path.GetDirectoryName(typeof(ObsRecorderHarnessDriver).Assembly.Location)
        ?? throw new InvalidOperationException("The test assembly has no location to find the harness next to.");

    internal static string HarnessPath => Path.Combine(HarnessDirectory, HarnessFileName);

    internal enum Verdict
    {
        Success,
        EncodeError,
        Failed,
        NotReported
    }

    // Ensures the helper sits next to the harness binary. The harness is built into the same output
    // directory as the test assembly, and the helper is copied there when the build layout does not
    // already satisfy the plugin — see ObsMuxerHelper.
    internal static bool EnsureHelperPresent()
    {
        var target = Path.Combine(HarnessDirectory, HelperFileName);
        if (File.Exists(target))
            return true;

        return ObsMuxerHelper.TryDeploy();
    }

    internal static (Verdict Verdict, int ExitCode) Run(string outputPath, double durationSeconds)
    {
        if (!File.Exists(HarnessPath))
            throw new InvalidOperationException(
                $"The recorder harness is not in the test output directory. Expected {HarnessPath}.");

        var startInfo = new ProcessStartInfo
        {
            FileName = HarnessPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(outputPath);
        startInfo.ArgumentList.Add(durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The recorder harness could not be started.");

        // Read to the end on another thread so a large log cannot deadlock the child.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
            throw new TimeoutException("The recorder harness did not exit in time.");

        var resultLine = stdout.Result
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.StartsWith("RESULT:", StringComparison.Ordinal));

        var verdict = Parse(resultLine);
        return (verdict, process.ExitCode);
    }

    private static Verdict Parse(string? line) => line switch
    {
        "RESULT:SUCCESS" => Verdict.Success,
        "RESULT:ENCODE_ERROR" => Verdict.EncodeError,
        null => Verdict.NotReported,
        _ => Verdict.Failed
    };

    // Kills every running obs-ffmpeg-mux helper. Only the helper started by the current recording
    // can be running: the tests are serial (assembly-level parallelization is off), so this cannot
    // reach a helper from a different test. The plugin spawns one helper per recording and keeps no
    // pool, so the set of running helpers is at most the one this test started.
    internal static void KillMuxerHelper()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pkill",
            ArgumentList = { "-9", "-x", HelperFileName },
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        using var process = Process.Start(startInfo);
        if (process is null)
            throw new InvalidOperationException("pkill could not be started.");

        process.WaitForExit(TimeSpan.FromSeconds(5));
    }
}
