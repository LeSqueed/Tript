// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Tript.Media;

// Finds the ffmpeg and ffprobe binaries the engine shells out to. The binaries are never bundled:
// they are located on PATH and verified to exist and run, failing with a clear error when absent.
// On the dev box they live at /usr/bin; on Windows they resolve the same way via PATH.
public sealed class FfmpegLocator
{
    // Overridable so tests can point at a fixture directory or a packaged runtime without touching
    // the environment. Null means "search PATH".
    public string? SearchDirectory { get; init; }

    public (string Ffmpeg, string Ffprobe) Locate()
    {
        var candidates = ResolveCandidates();
        var ffmpeg = candidates.FirstOrDefault(c => c.Name == "ffmpeg");
        var ffprobe = candidates.FirstOrDefault(c => c.Name == "ffprobe");

        if (ffmpeg is null)
            throw new FfmpegNotFoundException("ffmpeg was not found. Install ffmpeg and ensure it is on PATH.");
        if (ffprobe is null)
            throw new FfmpegNotFoundException("ffprobe was not found. Install ffmpeg and ensure it is on PATH.");

        // A binary that exists on disk but fails to run is nearly as useless as a missing one, and
        // a missing executable bit produces a Process start error that reads as a permissions bug.
        VerifyRuns(ffmpeg.Path, "ffmpeg");
        VerifyRuns(ffprobe.Path, "ffprobe");

        return (ffmpeg.Path, ffprobe.Path);
    }

    private sealed record Candidate(string Name, string Path);

    private List<Candidate> ResolveCandidates()
    {
        var names = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "ffmpeg.exe", "ffprobe.exe" }
            : new[] { "ffmpeg", "ffprobe" };

        var found = new List<Candidate>();
        if (SearchDirectory is not null)
        {
            foreach (var name in names)
            {
                var path = Path.Combine(SearchDirectory, name);
                if (File.Exists(path))
                    found.Add(new Candidate(name, path));
            }

            return found;
        }

        // Walk PATH exactly the way a shell would: the first executable hit wins. PATH entries are
        // colon-separated on Unix, semicolon-separated on Windows.
        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':',
                StringSplitOptions.RemoveEmptyEntries);

        foreach (var entry in pathEntries)
        {
            var directory = entry.Trim('"');
            if (directory.Length == 0) continue;

            foreach (var name in names)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                    found.Add(new Candidate(name, candidate));
            }
        }

        return found;
    }

    private static void VerifyRuns(string binary, string what)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = binary,
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }
            };

            if (!process.Start())
                throw new FfmpegNotFoundException($"{what} at '{binary}' could not be started.");

            // -version exits immediately; a hung binary is a broken install worth surfacing.
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                throw new FfmpegNotFoundException($"{what} at '{binary}' did not respond to -version within 5s.");
            }

            if (process.ExitCode != 0)
                throw new FfmpegNotFoundException($"{what} at '{binary}' exited with code {process.ExitCode} on -version.");
        }
        catch (FfmpegNotFoundException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new FfmpegNotFoundException($"Could not run {what} at '{binary}': {ex.Message}", ex);
        }
    }
}

public sealed class FfmpegNotFoundException : Exception
{
    public FfmpegNotFoundException(string message) : base(message) { }
    public FfmpegNotFoundException(string message, Exception inner) : base(message, inner) { }
}
