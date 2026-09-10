// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;

namespace Tript.Obs.IntegrationTests;

internal sealed class ObsRecorderHarnessDriver
{
    private const string HarnessFileName = "Tript.RecorderHarness";
    private const string HelperFileName = ObsMuxerHelper.HelperFileName;
    private const int HarnessNoDisplayExitCode = 5;

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

    internal static bool EnsureHelperPresent() => ObsMuxerHelper.TryDeploy(HarnessDirectory);

    internal static void RequireHelper()
    {
        if (EnsureHelperPresent())
            return;

        throw new Xunit.SkipException(
            $"No {HelperFileName} to record with. OBS ships it as a private plugin helper; none was "
            + $"found beside this machine's obs-ffmpeg plugin, and none could be placed in {HarnessDirectory}.");
    }

    internal static void RequireHarnessFoundADisplay(int exitCode, string stderr = "")
    {
        if (exitCode != HarnessNoDisplayExitCode)
            return;

        throw new Xunit.SkipException(
            $"The recorder harness found no reachable X server. {stderr.Trim()}".TrimEnd());
    }

    internal static (Verdict Verdict, int ExitCode) Run(string outputPath, double durationSeconds) =>
        RunCore(outputPath, durationSeconds, multiTrackCount: null, useRecorder: false);

    internal static (Verdict Verdict, int ExitCode) RunRecorder(string outputPath, double durationSeconds) =>
        RunCore(outputPath, durationSeconds, multiTrackCount: null, useRecorder: true);

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

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
            throw new TimeoutException("The recorder harness did not exit in time.");

        RequireHarnessFoundADisplay(process.ExitCode, stderr.Result);

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
