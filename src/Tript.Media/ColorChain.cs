// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Media;

// Builds the ffmpeg filter-graph fragments that handle colour. The chain text lives here, separate
// from the engine, so the exact five-stage order is one place a rewrite can lose — the spec names
// it as the part most likely to be lost, because each step looks individually optional.
internal static class ColorChain
{
    // The five-stage tone-map chain, in this order, not interchangeable:
    //   1. Linearise, with a nominal peak luminance of 100
    //   2. Convert to floating-point linear RGB (gbrpf32le is the actual planar float RGB format)
    //   3. Convert primaries to BT.709
    //   4. Tone-map with the Hable operator
    //   5. Return to BT.709 transfer, matrix and limited range
    // Then yuv420p unless the encoder is VAAPI, which supplies its own format handling.
    //
    // zscale reads the frame's colour metadata to convert; to make it convert HDR PQ to BT.709 the
    // frame must be actual RGB pixels (gbrpf32le), not merely tagged as RGB — setparams only rewrites
    // metadata, which on a YUV frame trips zscale ("YUV color family cannot have RGB matrix
    // coefficients"). Measured on ffmpeg n9.
    public const string ToneMapChain =
        "zscale=t=linear:npl=100,"
        + "format=gbrpf32le,"
        + "zscale=p=bt709,"
        + "tonemap=hable:desat=0,"
        + "zscale=t=bt709:m=bt709:r=tv,"
        + "format=yuv420p";

    // The SDR-with-mixed-segments path: no tone mapping, just an explicit yuv420p.
    public const string ForceYuv420p = "format=yuv420p";

    // Tags SDR output as BT.709. Top-level -color_trc/-color_primaries flags do NOT make libx264
    // write the VUI fields into the bitstream (measured — only the matrix landed); setparams in the
    // filter graph does, for both libx264 and libx265. Without explicit colour metadata, players
    // and upload pipelines guess, and guess differently from one another.
    public const string TagBt709 = "setparams=colorspace=bt709:color_primaries=bt709:color_trc=bt709";

    // Tags a preserved HDR output with bt2020nc / bt2020 / the source's own transfer (supplied by
    // the caller). Same setparams mechanism as the SDR tag.
    public static string TagPreserved(string transfer) =>
        $"setparams=colorspace=bt2020nc:color_primaries=bt2020:color_trc={transfer}";
}
