// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Buffers.Binary;
using System.IO.Compression;

namespace Tript.Detection;

internal sealed class GrayPngImage
{
    internal required int Width { get; init; }
    internal required int Height { get; init; }
    internal required byte[] Pixels { get; init; }
}

internal static class PngImageDecoder
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    internal static GrayPngImage Decode(ReadOnlySpan<byte> png)
    {
        if (png.Length < Signature.Length || !png[..Signature.Length].SequenceEqual(Signature))
            throw new InvalidDataException("The training sample is not a PNG image.");

        var offset = Signature.Length;
        var idat = new MemoryStream();
        byte[]? palette = null;
        var width = 0;
        var height = 0;
        var bitDepth = 0;
        var colorType = 0;
        var interlace = 0;
        var hasHeader = false;

        while (offset + 12 <= png.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(png[offset..]);
            offset += 4;
            if (length > int.MaxValue || offset + 4 + length + 4 > png.Length)
                throw new InvalidDataException("The PNG contains an invalid chunk.");

            var type = png.Slice(offset, 4);
            offset += 4;
            var dataLength = (int)length;
            var data = png.Slice(offset, dataLength);
            offset += dataLength;
            offset += 4;

            if (type.SequenceEqual("IHDR"u8))
            {
                if (data.Length != 13)
                    throw new InvalidDataException("The PNG header is invalid.");
                width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data));
                height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]));
                bitDepth = data[8];
                colorType = data[9];
                interlace = data[12];
                hasHeader = width > 0 && height > 0;
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                palette = data.ToArray();
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                idat.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }
        }

        if (!hasHeader || idat.Length == 0)
            throw new InvalidDataException("The PNG is missing image data.");
        if (bitDepth != 8 || interlace != 0)
            throw new InvalidDataException("Only non-interlaced 8-bit PNG samples are supported.");

        var bytesPerPixel = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => 0,
        };
        if (bytesPerPixel == 0 || (colorType == 3 && (palette is null || palette.Length % 3 != 0)))
            throw new InvalidDataException("The PNG uses an unsupported color format.");

        var rowBytes = checked(width * bytesPerPixel);
        var filteredLength = checked((rowBytes + 1) * height);
        var filtered = new byte[filteredLength];
        idat.Position = 0;
        using (var decompressor = new ZLibStream(idat, CompressionMode.Decompress, leaveOpen: true))
        {
            var total = 0;
            while (total < filtered.Length)
            {
                var read = decompressor.Read(filtered, total, filtered.Length - total);
                if (read == 0) break;
                total += read;
            }
            if (total != filtered.Length)
                throw new InvalidDataException("The PNG image data is truncated.");
        }

        var pixels = new byte[checked(width * height)];
        var previous = new byte[rowBytes];
        var current = new byte[rowBytes];
        var sourceOffset = 0;
        for (var y = 0; y < height; y++)
        {
            var filter = filtered[sourceOffset++];
            filtered.AsSpan(sourceOffset, rowBytes).CopyTo(current);
            sourceOffset += rowBytes;
            Unfilter(current, previous, bytesPerPixel, filter);

            for (var x = 0; x < width; x++)
                pixels[y * width + x] = ToGray(current, x * bytesPerPixel, colorType, palette);

            (current, previous) = (previous, current);
        }

        return new GrayPngImage { Width = width, Height = height, Pixels = pixels };
    }

    private static void Unfilter(byte[] row, byte[] previous, int bytesPerPixel, int filter)
    {
        if (filter == 0) return;
        if (filter is < 1 or > 4)
            throw new InvalidDataException("The PNG uses an unsupported row filter.");

        for (var index = 0; index < row.Length; index++)
        {
            var left = index >= bytesPerPixel ? row[index - bytesPerPixel] : (byte)0;
            var up = previous[index];
            var upLeft = index >= bytesPerPixel ? previous[index - bytesPerPixel] : (byte)0;
            row[index] = unchecked((byte)(row[index] + (filter switch
            {
                1 => left,
                2 => up,
                3 => (left + up) / 2,
                4 => Paeth(left, up, upLeft),
                _ => 0,
            })));
        }
    }

    private static byte Paeth(byte left, byte up, byte upLeft)
    {
        var estimate = left + up - upLeft;
        var leftDistance = Math.Abs(estimate - left);
        var upDistance = Math.Abs(estimate - up);
        var upLeftDistance = Math.Abs(estimate - upLeft);
        return leftDistance <= upDistance && leftDistance <= upLeftDistance
            ? left
            : upDistance <= upLeftDistance ? up : upLeft;
    }

    private static byte ToGray(byte[] row, int offset, int colorType, byte[]? palette)
    {
        return colorType switch
        {
            0 or 4 => row[offset],
            2 or 6 => (byte)(0.299f * row[offset] + 0.587f * row[offset + 1] + 0.114f * row[offset + 2]),
            3 => PaletteGray(row[offset], palette!),
            _ => throw new InvalidDataException("The PNG uses an unsupported color format."),
        };
    }

    private static byte PaletteGray(byte paletteIndex, byte[] palette)
    {
        var offset = paletteIndex * 3;
        if (offset + 2 >= palette.Length)
            throw new InvalidDataException("The PNG references a missing palette entry.");
        return (byte)(0.299f * palette[offset] + 0.587f * palette[offset + 1]
            + 0.114f * palette[offset + 2]);
    }
}
