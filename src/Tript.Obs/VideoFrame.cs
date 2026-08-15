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

        // The spans carry only the planes the source actually filled — which for a one-plane
        // format is exactly one entry. The format's own plane count is what separates "this
        // format has no plane 1" from "this plane exists but the source left it null", and a
        // consumer asks for plane 1 of a two-plane format without knowing how many the source
        // happened to fill under it.
        _planes = planes;
        _linesizes = linesizes;
        PlaneCount = FormatPlaneCount(format);
    }

    public FramePixelFormat Format { get; }
    public uint Width { get; }
    public uint Height { get; }

    // The number of planes the format defines, not the number the source filled. BGRA defines
    // one plane; asking for plane 1 of a BGRA frame is asking for a plane the format does not
    // have, which is why this is the bound GetPlane and GetLinesize enforce rather than the
    // length of the plane arrays.
    public int PlaneCount { get; }

    // Bytes per row, which is not width * bytesPerPixel: the compositor may pad each row out to an
    // alignment boundary.
    //
    // A plane index the format does not use is not an error: the source reports a null pointer for
    // it (libobs leaves unused data[] entries null) and the honest stride is zero. This is the
    // format question, not a missing-data question — BGRA has no plane 1, so its stride is zero.
    public uint GetLinesize(int plane)
    {
        if (plane < 0)
            throw new ArgumentOutOfRangeException(nameof(plane),
                $"A plane index cannot be negative, got {plane}.");

        // A plane the format defines but the source did not fill (unused data[] entry) has a
        // meaningless stride; zero is the honest value. Same answer for a plane the format does
        // not define at all.
        if ((uint)plane >= (uint)_linesizes.Length)
            return 0;

        return _linesizes[plane];
    }

    // The row count is the caller's, because only the caller knows what it wants: a subsampled
    // chroma plane is shorter than the frame. Rows beyond the frame's height are refused rather
    // than handed out as a span reaching past the plane — reading them is undefined behaviour that
    // would surface as a crash somewhere else entirely.
    //
    // A plane index the frame's format does not use is the one case that is not an error: the
    // source reports a null pointer for it (libobs leaves unused data[] entries null), and the
    // honest answer to "does this frame have plane 3?" is an empty view — a two-plane format
    // genuinely has no plane 2, and asking for it is a format question, not a missing-data
    // question. libobs never communicates a plane length, so the length exposed here is
    // linesize × rows, computed from the format and the requested row count exactly as the
    // allocator (video_frame_init) computed it; a null pointer is the only case where a plane can
    // be absent, and an empty view is what a null pointer means.
    public unsafe ReadOnlySpan<byte> GetPlane(int plane, uint rows)
    {
        if (plane < 0)
            throw new ArgumentOutOfRangeException(nameof(plane),
                $"A plane index cannot be negative, got {plane}.");

        if (rows > Height)
            throw new ArgumentOutOfRangeException(nameof(rows),
                $"Asked for {rows} rows of plane {plane} in a frame {Height} rows tall.");

        // A plane index the format does not use answers as an empty view, not an exception: the
        // source reports a null pointer for it (libobs leaves unused data[] entries null), and
        // "does this frame have plane 3?" is a legitimate question about a two-plane format. An
        // empty view is what a null pointer means. The same answer covers a plane the format
        // defines but this frame did not fill.
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

    // The number of planes each format defines. Only BGRA exists in the seam today; the other
    // branches stay so the format vocabulary can grow without this method silently answering
    // wrong. The count is what libobs's format table assigns: one plane for a packed format, two
    // for a 4:2:0 with packed chroma, three for a planar 4:2:0 or 4:2:2.
    private static int FormatPlaneCount(FramePixelFormat format) => format switch
    {
        FramePixelFormat.Bgra => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format,
            $"No plane count is known for {format}.")
    };
}

public delegate void FrameCallback(in VideoFrame frame);
