// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Media;

internal static class ColorChain
{
    public const string ToneMapChain =
        "zscale=t=linear:npl=75,"
        + "format=gbrpf32le,"
        + "zscale=p=bt709,"
        + "tonemap=hable:desat=0,"
        + "zscale=t=bt709:m=bt709:r=tv,"
        + "format=yuv420p,"
        + "eq=contrast=1.05:saturation=1.05:gamma=0.99";

    public const string ForceYuv420p = "format=yuv420p";

    public const string TagBt709 = "setparams=colorspace=bt709:color_primaries=bt709:color_trc=bt709";

    public static string TagPreserved(string transfer) =>
        $"setparams=colorspace=bt2020nc:color_primaries=bt2020:color_trc={transfer}";
}
