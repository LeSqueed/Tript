// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Tript.Media;

// Finds the ffmpeg and ffprobe binaries the engine shells out to. Today they are a system
// dependency on both platforms, resolved on PATH. A bundled copy is probed first so that shipping
// one later is a build change alone — nothing is assembled into vendor/ffmpeg/ yet, so on every
// current build this falls straight through to PATH. Either way the binaries are verified to exist
// and run, failing with a clear error when absent.
public sealed class FfmpegLocator
{
    // The layout a bundled ffmpeg would use: <app root>/vendor/ffmpeg/{ffmpeg,ffprobe}[.exe]. No
    // build step populates it at present; it is probed so that adding one needs no code change.
    private static readonly string VendorDirectory =
        Path.Combine(AppContext.BaseDirectory, "vendor", "ffmpeg");

    // Overridable so tests can point at a fixture directory or a packaged runtime without touching
    // the environment. Null means "search the vendor directory, then PATH".
    public string? SearchDirectory { get; init; }

    public (string Ffmpeg, string Ffprobe) Locate()
    {
        var candidates = ResolveCandidates();
        // Match on the name WITHOUT the platform extension: candidates are registered under
        // whatever name they were searched for ("ffmpeg" on Unix, "ffmpeg.exe" on Windows), and a
        // filter on the bare name silently matched nothing on Windows.
        var ffmpeg = candidates.FirstOrDefault(c =>
            Path.GetFileNameWithoutExtension(c.Name).Equals("ffmpeg", StringComparison.OrdinalIgnoreCase));
        var ffprobe = candidates.FirstOrDefault(c =>
            Path.GetFileNameWithoutExtension(c.Name).Equals("ffprobe", StringComparison.OrdinalIgnoreCase));

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

        if (SearchDirectory is not null)
        {
            return CollectDirectories(new[] { SearchDirectory }, names);
        }

        // A vendored copy first, so that once one is bundled it is the exact build the app was
        // assembled with and a stray older ffmpeg on PATH can never win. The directory is absent on
        // every current build, so today this always falls through to PATH.
        return CollectDirectories(new[] { VendorDirectory }, names)
            .Concat(CollectDirectories(PathDirectories(), names))
            .ToList();
    }

    private static IEnumerable<string> PathDirectories()
    {
        // Walk PATH exactly the way a shell would: the first executable hit wins. PATH entries are
        // colon-separated on Unix, semicolon-separated on Windows.
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
