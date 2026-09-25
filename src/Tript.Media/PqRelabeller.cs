// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Media;

public sealed record PqRelabelOutcome(bool Labelled, string? Failure)
{
    internal static PqRelabelOutcome Done { get; } = new(true, null);

    internal static PqRelabelOutcome Failed(string reason) => new(false, reason);
}

public static class PqRelabeller
{
    private const string Bt2020Primaries = "9";
    private const string PqTransfer = "16";
    private const string Bt709Matrix = "1";

    private static readonly TimeSpan Timeout = TimeSpan.FromHours(1);

    public static IReadOnlyList<string>? Arguments(string codecName, string input, string output)
    {
        var filter = codecName switch
        {
            "h264" => $"h264_metadata=colour_primaries={Bt2020Primaries}:transfer_characteristics={PqTransfer}:matrix_coefficients={Bt709Matrix}",
            "hevc" => $"hevc_metadata=colour_primaries={Bt2020Primaries}:transfer_characteristics={PqTransfer}:matrix_coefficients={Bt709Matrix}",
            "av1" => $"av1_metadata=color_primaries={Bt2020Primaries}:transfer_characteristics={PqTransfer}:matrix_coefficients={Bt709Matrix}",
            _ => null,
        };

        if (filter is null)
            return null;

        return
        [
            "-hide_banner", "-nostdin", "-y",
            "-i", input,
            "-map", "0", "-c", "copy",
            "-bsf:v", filter,
            "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt709",
            output,
        ];
    }

    public static PqRelabelOutcome Relabel(string ffmpegPath, MediaProbe probe, string path)
    {
        ArgumentNullException.ThrowIfNull(probe);

        MediaInfo before;
        try
        {
            before = probe.Probe(path);
        }
        catch (Exception exception) when (exception is ClipSourceException or IOException)
        {
            return PqRelabelOutcome.Failed($"the recording could not be read: {exception.Message}");
        }

        if (before.IsHdr)
            return PqRelabelOutcome.Done;

        var temporary = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".pq.mp4");
        if (Arguments(before.CodecName, path, temporary) is not { } arguments)
            return PqRelabelOutcome.Failed($"{before.CodecName} cannot be relabelled without re-encoding");

        try
        {
            var outcome = FfmpegRunner.RunBounded(ffmpegPath, arguments, Timeout);
            if (!outcome.Succeeded || !File.Exists(temporary))
                return PqRelabelOutcome.Failed($"ffmpeg failed: {FfmpegRunner.Tail(outcome.StandardError)}");
            if (!probe.Probe(temporary).IsHdr)
                return PqRelabelOutcome.Failed("the relabelled copy did not read back as PQ");

            File.Move(temporary, path, overwrite: true);
            return PqRelabelOutcome.Done;
        }
        catch (Exception exception) when (exception is ClipSourceException or IOException
                                              or UnauthorizedAccessException)
        {
            return PqRelabelOutcome.Failed(exception.Message);
        }
        finally
        {
            TryDelete(temporary);
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
