// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;

namespace Tript.Recorder;

internal static class DisplayColourResolver
{
    internal readonly record struct Choice(ObsSourceColorSpace? ColourSpace, float SdrWhiteLevelNits);

    internal static Choice Choose(IReadOnlyList<ObsRuntime.DisplayColour> probes)
    {
        ArgumentNullException.ThrowIfNull(probes);

        if (probes.Count == 0)
            return new Choice(null, 0f);

        var hdrDisplay = FirstMatch(probes, probe => HdrPlanner.IsHdr(probe.ColorSpace));
        var whiteLevelDisplay = hdrDisplay ?? FirstMatch(probes, probe => probe.SdrWhiteLevelNits > 0f);

        return new Choice(
            hdrDisplay?.ColorSpace ?? probes[0].ColorSpace,
            whiteLevelDisplay?.SdrWhiteLevelNits ?? 0f);
    }

    private static ObsRuntime.DisplayColour? FirstMatch(
        IReadOnlyList<ObsRuntime.DisplayColour> probes, Func<ObsRuntime.DisplayColour, bool> match)
    {
        foreach (var probe in probes)
        {
            if (match(probe))
                return probe;
        }

        return null;
    }
}
