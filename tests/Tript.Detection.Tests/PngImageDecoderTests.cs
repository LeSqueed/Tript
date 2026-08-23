// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Detection;
using Xunit;

namespace Tript.Detection.Tests;

public sealed class PngImageDecoderTests
{
    [Fact]
    public void Decode_reads_an_eight_bit_png_into_grayscale_pixels()
    {
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

        var image = PngImageDecoder.Decode(png);

        Assert.Equal(1, image.Width);
        Assert.Equal(1, image.Height);
        Assert.Single(image.Pixels);
    }
}
