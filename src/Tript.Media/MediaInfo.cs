// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Media;

// What probing a source file discovers, in the terms the clip decision needs. Parsed from ffprobe's
// JSON output; the parser is the only place ffprobe's exact field names and value spellings appear,
// so a probe shape change is a one-file fix.
public sealed class MediaInfo
{
    public required string CodecName { get; init; }

    // The codec's profile string as ffprobe reports it ("Main 10", "High", ...). Used to decide the
    // preserve path's HEVC profile requirement.
    public string? Profile { get; init; }

    public required int Width { get; init; }
    public required int Height { get; init; }

    // Frame rate as a rational number of frames per second. Kept rational so the "clip frame rate
    // matches the source" comparison for the stream-copy optimisation is exact rather than an
    // equality on rounded doubles.
    public required Fraction FrameRate { get; init; }

    // The pixel format ffprobe reports. "yuv420p10le" is the 10-bit signal preservation cares about.
    public required string PixelFormat { get; init; }

    // The colour description. Absent from the JSON when the file does not carry them — the spec's
    // probe normalises that to "unspecified", and so do we. An HDR source is one whose transfer is
    // smpte2084 (PQ) or arib-std-b67 (HLG); anything else is SDR.
    public required string ColorSpace { get; init; }
    public required string ColorTransfer { get; init; }
    public required string ColorPrimaries { get; init; }
    public required string ColorRange { get; init; }

    // The container's reported duration in seconds.
    public required double DurationSeconds { get; init; }

    // The number of audio streams in the file, in stream order. Multi-track recording is a
    // first-class feature; a clip must keep every audio track unless the user muted or re-levelled
    // one, and losing a track is silent, so the count is part of the probed contract.
    public required int AudioStreamCount { get; init; }

    public bool IsHdr => ColorTransfer is "smpte2084" or "arib-std-b67";

    // ffprobe reports transfers/primaries/matrices as enum names it does not always emit for older
    // files. A file with an explicit "unknown"/"unspecified" is not HDR.
    public static bool IsHdrTransfer(string? transfer) => transfer is "smpte2084" or "arib-std-b67";
}

// A rational number, as ffmpeg reports frame rates. The denominator is what makes "same frame rate"
// an exact comparison.
public readonly record struct Fraction(long Numerator, long Denominator)
{
    public double Value => Denominator == 0 ? double.NaN : (double)Numerator / Denominator;

    public static bool Same(Fraction a, Fraction b)
    {
        // Reduce both to canonical form so 30/1 and 60/2 compare equal.
        var (aNum, aDen) = Reduce(a);
        var (bNum, bDen) = Reduce(b);
        return aNum == bNum && aDen == bDen;
    }

    private static (long, long) Reduce(Fraction f)
    {
        var (n, d) = f;
        if (d == 0) return (n, d);
        var g = Gcd(Math.Abs(n), Math.Abs(d));
        if (n < 0) { n = -n; d = -d; }
        return (n / g, d / g);
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }
}
