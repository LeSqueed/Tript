// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Media;

public sealed class MediaInfo
{
    public required string CodecName { get; init; }

    public string? Profile { get; init; }

    public required int Width { get; init; }
    public required int Height { get; init; }

    public required Fraction FrameRate { get; init; }

    public required string PixelFormat { get; init; }

    public required string ColorSpace { get; init; }
    public required string ColorTransfer { get; init; }
    public required string ColorPrimaries { get; init; }
    public required string ColorRange { get; init; }

    public required double DurationSeconds { get; init; }

    public required int AudioStreamCount { get; init; }

    public bool IsHdr => ColorTransfer is "smpte2084" or "arib-std-b67";

    public static bool IsHdrTransfer(string? transfer) => transfer is "smpte2084" or "arib-std-b67";
}

public readonly record struct Fraction(long Numerator, long Denominator)
{
    public double Value => Denominator == 0 ? double.NaN : (double)Numerator / Denominator;

    public static bool Same(Fraction a, Fraction b)
    {
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
