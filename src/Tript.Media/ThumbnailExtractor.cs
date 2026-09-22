// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Core;

namespace Tript.Media;

public interface IThumbnailExtractor
{
    bool TryExtract(string sourcePath, string destinationPath);
}

public sealed class FfmpegThumbnailExtractor : IThumbnailExtractor
{
    private static readonly TimeSpan SeekOffset = TimeSpan.FromSeconds(1);

    private const double SeekFraction = 0.3;

    private const int Width = 480;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly string _ffmpegPath;
    private readonly string? _ffprobePath;
    private readonly Lazy<MediaProbe?> _probe;

    public FfmpegThumbnailExtractor(string ffmpegPath, string? ffprobePath = null)
    {
        _ffmpegPath = ffmpegPath;
        _ffprobePath = ffprobePath;
        _probe = new Lazy<MediaProbe?>(
            () => _ffprobePath is null ? null : new MediaProbe(_ffprobePath),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool TryExtract(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath))
            return false;

        var info = TryProbe(sourcePath);
        var filter = VideoFilter(info?.IsHdr == true);
        return TryExtractAt(sourcePath, destinationPath, ChosenOffset(info), filter, rejectBlack: true)
            || TryExtractAt(sourcePath, destinationPath, TimeSpan.Zero, filter, rejectBlack: false);
    }

    private MediaInfo? TryProbe(string sourcePath)
    {
        try
        {
            return _probe.Value?.Probe(sourcePath);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static TimeSpan ChosenOffset(MediaInfo? info)
    {
        if (info?.DurationSeconds is > 0 and var seconds && double.IsFinite(seconds))
            return TimeSpan.FromSeconds(Math.Max(SeekOffset.TotalSeconds, seconds * SeekFraction));

        return SeekOffset;
    }

    private static string VideoFilter(bool hdr)
    {
        var filter = $"blackframe=amount=98:threshold=32,scale={Width}:-2";
        return hdr ? $"{filter},{ColorChain.ToneMapChain}" : filter;
    }

    private bool TryExtractAt(string sourcePath, string destinationPath, TimeSpan offset, string filter, bool rejectBlack)
    {
        var arguments = new List<string>
        {
            "-nostdin",
            "-y",

            "-loglevel", "info",
        };

        if (offset > TimeSpan.Zero)
        {
            arguments.Add("-ss");
            arguments.Add(offset.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }

        arguments.AddRange([
            "-i", sourcePath,

            "-map", "0:v:0",
            "-frames:v", "1",
            "-an", "-sn", "-dn",

            "-vf", filter,

            "-f", "image2", "-update", "1",
            "-c:v", "mjpeg",
            "-q:v", "4",
            destinationPath,
        ]);

        var outcome = FfmpegRunner.RunBounded(_ffmpegPath, arguments, Timeout);

        if (outcome.Succeeded && HasContent(destinationPath)
            && (!rejectBlack || !outcome.StandardError.Contains("blackframe", StringComparison.OrdinalIgnoreCase)))
            return true;

        // A black frame is expected and the caller retries at another offset, so only a genuine
        // ffmpeg failure is worth reporting. That is the only record of why a thumbnail is blank.
        if (!outcome.Succeeded)
        {
            Diagnostics.Report(DiagnosticLevel.Warning,
                $"ffmpeg could not extract a thumbnail from '{Path.GetFileName(sourcePath)}' at {offset.TotalSeconds:0.#}s "
                + $"(exit {outcome.ExitCode}): {FfmpegRunner.Tail(outcome.StandardError)}");
        }

        TryDelete(destinationPath);
        return false;
    }

    private static bool HasContent(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
