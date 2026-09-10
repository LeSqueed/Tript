// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Obs;

public enum FramePixelFormat
{
    Bgra
}

public readonly ref struct VideoFrame
{
    private readonly ReadOnlySpan<nint> _planes;
    private readonly ReadOnlySpan<uint> _linesizes;

    public VideoFrame(FramePixelFormat format, uint width, uint height,
        ReadOnlySpan<nint> planes, ReadOnlySpan<uint> linesizes)
    {
        if (planes.Length != linesizes.Length)
            throw new ArgumentException(
                $"A frame has one linesize per plane, got {planes.Length} plane(s) and {linesizes.Length} linesize(s).",
                nameof(linesizes));

        Format = format;
        Width = width;
        Height = height;

        _planes = planes;
        _linesizes = linesizes;
        PlaneCount = FormatPlaneCount(format);
    }

    public FramePixelFormat Format { get; }
    public uint Width { get; }
    public uint Height { get; }

    public int PlaneCount { get; }

    public uint GetLinesize(int plane)
    {
        if (plane < 0)
            throw new ArgumentOutOfRangeException(nameof(plane),
                $"A plane index cannot be negative, got {plane}.");

        if ((uint)plane >= (uint)_linesizes.Length)
            return 0;

        return _linesizes[plane];
    }

    public unsafe ReadOnlySpan<byte> GetPlane(int plane, uint rows)
    {
        if (plane < 0)
            throw new ArgumentOutOfRangeException(nameof(plane),
                $"A plane index cannot be negative, got {plane}.");

        if (rows > Height)
            throw new ArgumentOutOfRangeException(nameof(rows),
                $"Asked for {rows} rows of plane {plane} in a frame {Height} rows tall.");

        if ((uint)plane >= (uint)_planes.Length)
            return ReadOnlySpan<byte>.Empty;

        var pointer = _planes[plane];
        if (pointer == 0)
            return ReadOnlySpan<byte>.Empty;

        var length = (long)_linesizes[plane] * rows;
        if (length > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(rows),
                $"{rows} rows of plane {plane} is {length} bytes, more than a span can address.");

        return new ReadOnlySpan<byte>((void*)pointer, (int)length);
    }

    private static int FormatPlaneCount(FramePixelFormat format) => format switch
    {
        FramePixelFormat.Bgra => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format,
            $"No plane count is known for {format}.")
    };
}

public delegate void FrameCallback(in VideoFrame frame);
