// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

#if TRIPT_TRAINING

namespace Tript.Media;

public interface ITrainingFrameExtractor
{
    bool TryExtract(string sourcePath, double timestampSeconds, string destinationPath);
}

public sealed class FfmpegTrainingFrameExtractor : ITrainingFrameExtractor
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly string _ffmpegPath;

    public string? LastError { get; private set; }

    public FfmpegTrainingFrameExtractor(string ffmpegPath)
    {
        _ffmpegPath = ffmpegPath;
    }

    public bool TryExtract(string sourcePath, double timestampSeconds, string destinationPath)
    {
        LastError = null;
        if (!double.IsFinite(timestampSeconds) || timestampSeconds < 0)
            return false;

        var fastSeekArguments = BuildArguments(sourcePath, timestampSeconds, destinationPath, seekBeforeInput: true);
        var outcome = FfmpegRunner.RunBounded(_ffmpegPath, fastSeekArguments, Timeout);
        if (outcome.Succeeded && HasContent(destinationPath))
            return true;

        TryDelete(destinationPath);
        var accurateSeekArguments = BuildArguments(sourcePath, timestampSeconds, destinationPath, seekBeforeInput: false);
        outcome = FfmpegRunner.RunBounded(_ffmpegPath, accurateSeekArguments, Timeout);
        if (outcome.Succeeded && HasContent(destinationPath))
            return true;

        LastError = string.IsNullOrWhiteSpace(outcome.StandardError)
            ? $"ffmpeg exited with code {outcome.ExitCode}."
            : outcome.StandardError.Trim();
        TryDelete(destinationPath);
        return false;
    }

    private static string[] BuildArguments(string sourcePath, double timestampSeconds, string destinationPath,
        bool seekBeforeInput)
    {
        var arguments = new List<string>
        {
            "-nostdin", "-y",
        };
        var seek = timestampSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        if (seekBeforeInput)
            arguments.AddRange(["-ss", seek]);
        arguments.Add("-i");
        arguments.Add(sourcePath);
        if (!seekBeforeInput)
            arguments.AddRange(["-ss", seek]);
        arguments.AddRange([
            "-map", "0:v:0",
            "-frames:v", "1",
            "-an", "-sn", "-dn",
            "-pix_fmt", "rgb24",
            "-c:v", "png",
            "-f", "image2",
            "-update", "1",
            destinationPath,
        ]);
        return arguments.ToArray();
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

#endif
