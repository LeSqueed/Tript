// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using System.Runtime.InteropServices;
using Tript.Core;

namespace Tript.Media;

public sealed class FfmpegLocator
{
    private static readonly string VendorDirectory =
        Path.Combine(AppContext.BaseDirectory, "vendor", "ffmpeg");

    public string? SearchDirectory { get; init; }

    public (string Ffmpeg, string Ffprobe) Locate()
    {
        var candidates = ResolveCandidates();

        var ffmpeg = candidates.FirstOrDefault(c =>
            Path.GetFileNameWithoutExtension(c.Name).Equals("ffmpeg", StringComparison.OrdinalIgnoreCase));
        var ffprobe = candidates.FirstOrDefault(c =>
            Path.GetFileNameWithoutExtension(c.Name).Equals("ffprobe", StringComparison.OrdinalIgnoreCase));

        if (ffmpeg is null)
            throw new FfmpegNotFoundException("ffmpeg was not found. Install ffmpeg and ensure it is on PATH.");
        if (ffprobe is null)
            throw new FfmpegNotFoundException("ffprobe was not found. Install ffmpeg and ensure it is on PATH.");

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

        if (SearchDirectory is not null)
        {
            return CollectDirectories(new[] { SearchDirectory }, names);
        }

        return CollectDirectories(new[] { VendorDirectory }, names)
            .Concat(CollectDirectories(PathDirectories(), names))
            .ToList();
    }

    private static IEnumerable<string> PathDirectories()
    {
        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':',
                StringSplitOptions.RemoveEmptyEntries);

        return pathEntries.Select(entry => entry.Trim('"')).Where(directory => directory.Length > 0);
    }

    private static List<Candidate> CollectDirectories(IEnumerable<string> directories, string[] names)
    {
        var found = new List<Candidate>();
        foreach (var directory in directories)
        {
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
            ChildProcessJob.Track(process);

            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch {  }
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
