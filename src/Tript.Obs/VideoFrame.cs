// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Obs;

public enum FramePixelFormat
{
    // One plane, 8 bits per channel, blue first. The only format the capture pipeline asks for
    // today; the plane-indexed accessors below exist so a planar format can be added without
    // reshaping the seam.
    Bgra
}

// Passed to the callback by reference and never copied: it is the hot path, one delivery per
// captured frame, and the plane pointers it carries are only valid for the duration of the call.
// A ref struct is what makes that lifetime a compile-time rule rather than a comment — the
// callback cannot stash the frame in a field, a closure or a queue.
public readonly ref struct VideoFrame
{
    private readonly ReadOnlySpan<nint> _planes;
    private readonly ReadOnlySpan<uint> _linesizes;

    // Pointers rather than spans because the source knows a plane's stride but not its allocated
    // length; nint rather than byte* because a pointer cannot be a generic type argument.
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
    }

    public FramePixelFormat Format { get; }
    public uint Width { get; }
    public uint Height { get; }
    public int PlaneCount => _planes.Length;

    // Bytes per row, which is not width * bytesPerPixel: the compositor may pad each row out to an
    // alignment boundary.
    public uint GetLinesize(int plane)
    {
        if ((uint)plane >= (uint)_linesizes.Length)
            throw new ArgumentOutOfRangeException(nameof(plane),
                $"Frame has {_linesizes.Length} plane(s), asked for plane {plane}.");

        return _linesizes[plane];
    }

    // The row count is the caller's, because only the caller knows what it wants: a subsampled
    // chroma plane is shorter than the frame. Rows beyond the frame's height are refused rather
    // than handed out as a span reaching past the plane — reading them is undefined behaviour that
    // would surface as a crash somewhere else entirely.
    public unsafe ReadOnlySpan<byte> GetPlane(int plane, uint rows)
    {
        if ((uint)plane >= (uint)_planes.Length)
            throw new ArgumentOutOfRangeException(nameof(plane),
                $"Frame has {_planes.Length} plane(s), asked for plane {plane}.");

        if (rows > Height)
            throw new ArgumentOutOfRangeException(nameof(rows),
                $"Asked for {rows} rows of plane {plane} in a frame {Height} rows tall.");

        var pointer = _planes[plane];
        if (pointer == 0)
            throw new InvalidOperationException($"Plane {plane} carries no data in this frame.");

        var length = (long)_linesizes[plane] * rows;
        if (length > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(rows),
                $"{rows} rows of plane {plane} is {length} bytes, more than a span can address.");

        return new ReadOnlySpan<byte>((void*)pointer, (int)length);
    }
}

public delegate void FrameCallback(in VideoFrame frame);
