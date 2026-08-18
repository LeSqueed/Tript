// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;

namespace Tript.Obs.IntegrationTests;

// Drives the Tript.RecorderHarness executable as a child process. The harness exists because the
// ffmpeg_muxer plugin spawns its obs-ffmpeg-mux helper next to the *actual binary* of the process
// (os_get_executable_path_ptr resolves /proc/self/exe, measured), and under `dotnet test` the
// process that would start the output is dotnet itself, which cannot have a helper dropped beside
// it.
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
        Failed
    }

    // Ensures the helper sits next to the harness binary. The harness is built into the same output
    // directory as the test assembly, and the helper is copied there when the build layout does not
    // already satisfy the plugin — see ObsMuxerHelper.
    internal static bool EnsureHelperPresent() => ObsMuxerHelper.TryDeploy(HarnessDirectory);

    // The prerequisite every recording test shares, as a skip rather than an assertion. A machine
    // with no OBS ffmpeg plugin installed cannot record at all, and reporting that as a failed test
    // is how a broken muxer lookup passed for a working machine's problem for weeks.
    internal static void RequireHelper()
    {
        if (EnsureHelperPresent())
            return;

        throw new Xunit.SkipException(
            $"No {HelperFileName} to record with. OBS ships it as a private plugin helper; none was "
            + $"found beside this machine's obs-ffmpeg plugin, and none could be placed in {HarnessDirectory}.");
    }

    internal static (Verdict Verdict, int ExitCode) Run(string outputPath, double durationSeconds) =>
        RunCore(outputPath, durationSeconds, multiTrackCount: null, useRecorder: false);

    // The recorder round-trip: the harness drives the T3 recorder state machine (Recorder over
    // ObsRecorderSession) rather than the raw binding. The parent asserts the file and the
    // recorder's verdict — the state machine's Idle -> Recording -> Stopping -> Idle against a real
    // muxer.
    internal static (Verdict Verdict, int ExitCode) RunRecorder(string outputPath, double durationSeconds) =>
        RunCore(outputPath, durationSeconds, multiTrackCount: null, useRecorder: true);

    // The multi-track round-trip: the harness wires the audio path through the routing service with
    // trackCount tracks, records, and reports success. The track count is verified from the file by
    // the caller's probe, not from the harness.
    internal static (Verdict Verdict, int ExitCode) RunMultiTrack(string outputPath, double durationSeconds, int trackCount) =>
        RunCore(outputPath, durationSeconds, multiTrackCount: trackCount, useRecorder: false);

    private static (Verdict Verdict, int ExitCode) RunCore(string outputPath, double durationSeconds, int? multiTrackCount, bool useRecorder)
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
        if (multiTrackCount is { } count)
        {
            startInfo.ArgumentList.Add("--multi-track");
            startInfo.ArgumentList.Add(count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        else if (useRecorder)
        {
            startInfo.ArgumentList.Add("--recorder");
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The recorder harness could not be started.");

        // Read to the end on another thread so a large log cannot deadlock the child.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
            throw new TimeoutException("The recorder harness did not exit in time.");

        // The harness prints a RESULT line for every outcome it recognises, so a missing one means
        // it bailed out earlier — and its own reason on stderr is the only useful thing to report.
        var resultLine = stdout.Result
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.StartsWith("RESULT:", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"The harness exited {process.ExitCode} without a RESULT line: {stderr.Result.Trim()}");

        return (Parse(resultLine), process.ExitCode);
    }

    private static Verdict Parse(string line) => line switch
    {
        "RESULT:SUCCESS" => Verdict.Success,
        "RESULT:ENCODE_ERROR" => Verdict.EncodeError,
        _ => Verdict.Failed
    };

    // Kills every running obs-ffmpeg-mux helper. Only the helper started by the current recording
    // can be running: the tests are serial (assembly-level parallelization is off), so this cannot
    // reach a helper from a different test.
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
