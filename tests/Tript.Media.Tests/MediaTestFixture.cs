// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using System.Globalization;
using Tript.Media;

namespace Tript.Media.Tests;

// Builds the synthetic media fixtures the clip tests run against, with ffmpeg itself so the suite
// is self-contained. Every fixture is created fresh in a per-run temp directory; nothing here reads
// a checked-in media file.
internal static class MediaTestFixture
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(60);

    // The ffmpeg/ffprobe the engine will shell out to. Located through the real locator so the
    // tests exercise the same discovery path the product uses.
    internal static readonly (string Ffmpeg, string Ffprobe) Binaries = Locate();

    private static (string, string) Locate()
    {
        var locator = new FfmpegLocator();
        return locator.Locate();
    }

    // A shared scratch root, one per test-run process.
    internal static readonly string ScratchRoot =
        Path.Combine(Path.GetTempPath(), "tript-media-tests", Guid.NewGuid().ToString("N"));

    static MediaTestFixture()
    {
        Directory.CreateDirectory(ScratchRoot);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteScratchRoot();
    }

    private static void DeleteScratchRoot()
    {
        try
        {
            Directory.Delete(ScratchRoot, recursive: true);
            var parent = Path.GetDirectoryName(ScratchRoot);
            if (parent is not null && Directory.Exists(parent) && Directory.GetFileSystemEntries(parent).Length == 0)
                Directory.Delete(parent);
        }
        catch (IOException)
        {
            // Best-effort cleanup of generated media fixtures.
        }
    }

    internal static string Run(string ffmpeg, IReadOnlyList<string> args)
    {
        var (_, stderr, exitCode) = RunCaptured(ffmpeg, args);
        if (exitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed ({exitCode}): {stderr}");
        return stderr;
    }

    private static (string Stdout, string Stderr, int ExitCode) RunCaptured(string executable,
        IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start {executable}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(ProcessTimeout))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            process.WaitForExit(TimeSpan.FromSeconds(10));
            throw new TimeoutException($"{executable} did not exit within {ProcessTimeout}.");
        }

        return (stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult(), process.ExitCode);
    }

    // A stand-in for the ffmpeg binary that exits 0 and writes either nothing or an empty file at
    // `writesEmptyFileAt`. Real ffmpeg reaches the same states (an out-of-range seek, a refused
    // overwrite) but only through argument shapes the engine's own validation rejects first, so the
    // stub is the only way to drive the exit-0-without-output path from a test.
    internal static string CreateStubFfmpeg(string name, string? writesEmptyFileAt = null)
    {
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(ScratchRoot, name + ".cmd");
            var touch = writesEmptyFileAt is null ? string.Empty : $"type nul > \"{writesEmptyFileAt}\"\r\n";
            File.WriteAllText(cmd, "@echo off\r\n" + touch + "exit /b 0\r\n");
            return cmd;
        }

        var script = Path.Combine(ScratchRoot, name + ".sh");
        var write = writesEmptyFileAt is null ? string.Empty : $": > '{writesEmptyFileAt}'\n";
        File.WriteAllText(script, "#!/bin/sh\n" + write + "exit 0\n");
        File.SetUnixFileMode(script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    // Quotes a single argument for the old string-based Arguments path. Kept for the fixture
    // builders that join args into one string; the probe helpers below use ArgumentList instead.
    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    // A plain SDR MP4 with a testsrc2 pattern and N audio tracks, each a distinct sine tone. The
    // video uses 1s keyframe interval (g=30 @ 30fps) so non-keyframe cut points exist. Returns the
    // path.
    internal static string CreateSdrSource(string name, double durationSeconds = 6, int audioTracks = 1)
    {
        var path = Path.Combine(ScratchRoot, name);
        var args = new List<string>
        {
            "-hide_banner", "-y",
            "-f", "lavfi", "-i", $"testsrc2=duration={durationSeconds.ToString(CultureInfo.InvariantCulture)}:size=320x240:rate=30",
        };

        for (var t = 0; t < audioTracks; t++)
        {
            var freq = 1000 + t * 500;
            args.Add("-f"); args.Add("lavfi");
            args.Add("-i"); args.Add($"sine=frequency={freq}:duration={durationSeconds.ToString(CultureInfo.InvariantCulture)}:sample_rate=48000");
        }

        args.Add("-map"); args.Add("0:v");
        for (var t = 0; t < audioTracks; t++)
        {
            args.Add("-map"); args.Add($"{t + 1}:a");
        }

        args.Add("-c:v"); args.Add("libx264");
        args.Add("-g"); args.Add("30");
        args.Add("-keyint_min"); args.Add("30");
        if (audioTracks > 0)
        {
            args.Add("-c:a"); args.Add("aac");
            args.Add("-b:a"); args.Add("128k");
        }

        args.Add(path);
        Run(Binaries.Ffmpeg, args);
        return path;
    }

    // A lossless SDR source (x264 qp 0) so an exact-time cut can be verified by comparing the
    // output's first frame hash to the source's frame at the cut time — lossless re-encode means
    // the hash comparison is exact.
    internal static string CreateLosslessSource(string name, double durationSeconds = 6)
    {
        var path = Path.Combine(ScratchRoot, name);
        var args = new List<string>
        {
            "-hide_banner", "-y",
            "-f", "lavfi", "-i", $"testsrc2=duration={durationSeconds.ToString(CultureInfo.InvariantCulture)}:size=320x240:rate=30",
            "-c:v", "libx264", "-qp", "0", "-g", "30", "-keyint_min", "30",
            path,
        };

        Run(Binaries.Ffmpeg, args);
        return path;
    }

    // A 10-bit HEVC fixture tagged as HDR (PQ/smpte2084, bt2020). The VUI colour params are forced
    // through x265's own options because the generic -color_trc flag alone does not make libx265
    // write the transfer/primaries into the bitstream (measured).
    internal static string CreateHdrSource(string name, double durationSeconds = 3)
    {
        var path = Path.Combine(ScratchRoot, name);
        var args = new List<string>
        {
            "-hide_banner", "-y",
            "-f", "lavfi", "-i", $"testsrc2=duration={durationSeconds.ToString(CultureInfo.InvariantCulture)}:size=640x360:rate=30",
            "-pix_fmt", "yuv420p10le",
            "-c:v", "libx265",
            "-profile:v", "main10",
            "-x265-params", "log-level=error:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc",
            path,
        };

        Run(Binaries.Ffmpeg, args);
        return path;
    }

    // Probing helpers the tests share. These go through ffprobe directly so assertions are about
    // the file's actual contents, not about what the engine chose to believe.

    internal static string ProbeValue(string ffprobe, string path, string streamSelector, string key)
    {
        // streamSelector is "v:0", "a:0", or "format". -show_entries uses "stream=..." for stream
        // keys and "format=..." for container keys; -select_streams narrows which stream.
        var args = streamSelector == "format"
            ? new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", path }
            : new[] { "-v", "error", "-select_streams", streamSelector, "-show_entries", $"stream={key}", "-of", "default=nw=1:nk=1", path };

        return RunCaptured(ffprobe, args).Stdout.Trim();
    }

    // The first frame's MD5 hash, for exact-time content comparisons on lossless sources.
    internal static string FirstFrameHash(string ffmpeg, string path)
    {
        var stdout = RunCaptured(ffmpeg,
            ["-hide_banner", "-v", "error", "-i", path, "-frames:v", "1", "-f", "framemd5", "-"]).Stdout;

        foreach (var line in stdout.Split('\n'))
        {
            if (line.StartsWith("0,") || line.StartsWith("1,"))
            {
                var parts = line.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length >= 6)
                    return parts[^1].Trim();
            }
        }

        throw new InvalidOperationException($"No frame hash in framemd5 output: {stdout}");
    }

    // PSNR in dB between the first frame of `pathA` and the first frame the source produces when
    // seeking to `seekSeconds`. Both first frames are extracted as raw yuv420p (single frames,
    // lossless), so the comparison is between decoded pixels — unaffected by how each file is
    // compressed.
    internal static double FirstFramePsnrDb(string ffmpeg, string pathA, string seekSeconds,
        string sourcePath, string scratchFilePrefix, int width = 320, int height = 240)
    {
        var frameA = Path.Combine(ScratchRoot, $"{scratchFilePrefix}-a.raw");
        var frameB = Path.Combine(ScratchRoot, $"{scratchFilePrefix}-b.raw");

        foreach (var (output, extraArgs) in new[]
                 {
                     (frameA, new[] { "-i", pathA }),
                     (frameB, new[] { "-ss", seekSeconds, "-i", sourcePath }),
                 })
        {
            var args = new List<string> { "-hide_banner", "-v", "error", "-y" };
            args.AddRange(extraArgs);
            args.AddRange(new[] { "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "yuv420p", output });
            Run(ffmpeg, args);
        }

        return RawPsnrDb(ffmpeg, frameA, frameB, width, height);
    }

    // PSNR between the first decoded frame of each file, extracted as raw yuv420p. Used to compare a
    // clip's first frame against a reference produced by running a known filter chain directly on the
    // source — a correct chain yields ~40+ dB, a wrong one substantially less.
    internal static double FirstFrameVsRawPsnrDb(string ffmpeg, string pathA, string pathB,
        int width, int height, string scratchFilePrefix)
    {
        var frameA = Path.Combine(ScratchRoot, $"{scratchFilePrefix}-a.raw");
        var frameB = Path.Combine(ScratchRoot, $"{scratchFilePrefix}-b.raw");

        foreach (var (output, extraArgs) in new[]
                 {
                     (frameA, new[] { "-i", pathA }),
                     (frameB, new[] { "-i", pathB }),
                 })
        {
            var args = new List<string> { "-hide_banner", "-v", "error", "-y" };
            args.AddRange(extraArgs);
            args.AddRange(new[] { "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "yuv420p", output });
            Run(ffmpeg, args);
        }

        return RawPsnrDb(ffmpeg, frameA, frameB, width, height);
    }

    private static double RawPsnrDb(string ffmpeg, string frameA, string frameB, int width, int height)
    {
        var stderr = RunCaptured(ffmpeg,
            ["-hide_banner", "-v", "info",
             "-f", "rawvideo", "-s", $"{width}x{height}", "-pix_fmt", "yuv420p", "-i", frameA,
             "-f", "rawvideo", "-s", $"{width}x{height}", "-pix_fmt", "yuv420p", "-i", frameB,
             "-filter_complex", "psnr", "-f", "null", "-"]).Stderr;

        foreach (var line in stderr.Split('\n'))
        {
            var idx = line.IndexOf("PSNR y:", StringComparison.Ordinal);
            if (idx >= 0)
            {
                var rest = line[(idx + "PSNR y:".Length)..];
                var end = rest.IndexOfAny([' ', '\r', '\n']);
                var raw = end >= 0 ? rest[..end] : rest;
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    return v;
            }
        }

        throw new InvalidOperationException($"No PSNR in output: {stderr}");
    }

    // A single audio stream's RMS level in dB, or double.NegativeInfinity for silence.
    internal static double AudioRmsDb(string ffmpeg, string path, int audioStreamIndex)
    {
        var stderr = RunCaptured(ffmpeg,
            ["-v", "info", "-i", path, "-map", $"0:a:{audioStreamIndex}",
             "-af", "astats=metadata=1:reset=0", "-f", "null", "-"]).Stderr;

        foreach (var line in stderr.Split('\n'))
        {
            var marker = "RMS level dB:";
            var idx = line.IndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0)
            {
                var raw = line[(idx + marker.Length)..].Trim();
                return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                    ? v
                    : double.NegativeInfinity;
            }
        }

        throw new InvalidOperationException($"No RMS level in astats output: {stderr}");
    }

    internal static double ProbeDuration(string ffprobe, string path)
    {
        var raw = ProbeValue(ffprobe, path, "format", "duration");
        return double.Parse(raw, CultureInfo.InvariantCulture);
    }
}
