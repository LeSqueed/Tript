// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using System.Text.Json;

namespace Tript.Obs.IntegrationTests;

// Runs scripts/probe-media.sh over a recording and hands back what ffprobe saw. The script lives in
// this repo and is found by walking up from the test assembly, so a machine that is not the author's
// gets a real probe rather than a silent null.
internal static class ProbeMediaScript
{
    private const string ScriptRelativePath = "scripts/probe-media.sh";

    // A probe that cannot run is a broken suite, not a test result: returning null used to satisfy
    // "the file is unparseable" without a probe ever having run.
    internal static JsonDocument Run(string file)
    {
        var script = Locate();

        var startInfo = new ProcessStartInfo
        {
            FileName = "bash",
            ArgumentList = { script, file },
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"{script} could not be started.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
            throw new TimeoutException($"{script} did not exit within 30 seconds for {file}.");

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{script} exited {process.ExitCode} for {file}: {stderr.Result}");

        try
        {
            return JsonDocument.Parse(stdout.Result);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"{script} produced no parseable output for {file}: {stdout.Result}", exception);
        }
    }

    private static string Locate()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, ScriptRelativePath);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new InvalidOperationException(
            $"{ScriptRelativePath} was not found above {AppContext.BaseDirectory}.");
    }
}
