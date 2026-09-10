// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

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

        return TryExtractAt(sourcePath, destinationPath, ChosenOffset(sourcePath), rejectBlack: true)
            || TryExtractAt(sourcePath, destinationPath, TimeSpan.Zero, rejectBlack: false);
    }

    private TimeSpan ChosenOffset(string sourcePath)
    {
        try
        {
            var duration = _probe.Value?.Probe(sourcePath).DurationSeconds;
            if (duration is > 0 and var seconds && double.IsFinite(seconds))
                return TimeSpan.FromSeconds(Math.Max(SeekOffset.TotalSeconds, seconds * SeekFraction));
        }
        catch (Exception)
        {
        }

        return SeekOffset;
    }

    private bool TryExtractAt(string sourcePath, string destinationPath, TimeSpan offset, bool rejectBlack)
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

            "-vf", $"blackframe=amount=98:threshold=32,scale={Width}:-2",

            "-f", "image2", "-update", "1",
            "-c:v", "mjpeg",
            "-q:v", "4",
            destinationPath,
        ]);

        var outcome = FfmpegRunner.RunBounded(_ffmpegPath, arguments, Timeout);

        if (outcome.Succeeded && HasContent(destinationPath)
            && (!rejectBlack || !outcome.StandardError.Contains("blackframe", StringComparison.OrdinalIgnoreCase)))
            return true;

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
