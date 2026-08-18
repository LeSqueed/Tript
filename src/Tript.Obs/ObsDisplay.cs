// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.RegularExpressions;

namespace Tript.Obs;

// One monitor a display-capture source will accept, described from that source's own property
// items. Id is the stable handle a setting stores: on Windows the opaque monitor_id device string
// monitor_capture matches on, on Linux/xshm the screen index as a string.
public sealed record ObsDisplay(string Id, string Name, int Index, int Width, int Height, bool Primary);

// The outcome of resolving a saved monitor preference. RequestedMissing is the whole reason this is
// not just an ObsDisplay: falling back is silent otherwise, and the user's monitor choice quietly
// not being honoured is exactly what has to be reported.
public readonly record struct ObsDisplayResolution(ObsDisplay? Selected, bool RequestedMissing);

// The capture plugins describe each monitor in one human string: win-capture's monitor_capture
// builds "<name>: <w>x<h> @ <x>,<y>" and linux-capture's xshm_input a "Screen N (<w>x<h> @ <x>,<y>)"
// of the same shape. Splitting it is how a monitor gets a size and a name without a second,
// platform-specific enumeration that would then have to be correlated with this one.
internal static partial class ObsDisplayLabel
{
    internal static (string Name, int Width, int Height, bool AtOrigin) Parse(string? label, int index)
    {
        var text = label?.Trim() ?? string.Empty;
        if (text.Length == 0)
            return ($"Display {index + 1}", 0, 0, false);

        var size = SizePattern().Match(text);
        if (!size.Success)
            return (text, 0, 0, false);

        var name = text[..size.Index].TrimEnd(' ', '\t', ':', '(', '[', '-', '–', '—');
        var position = PositionPattern().Match(text, size.Index + size.Length);

        return (
            name.Length > 0 ? name : text,
            int.Parse(size.Groups[1].ValueSpan),
            int.Parse(size.Groups[2].ValueSpan),
            position.Success && position.Groups[1].ValueSpan is "0" && position.Groups[2].ValueSpan is "0");
    }

    [GeneratedRegex(@"(\d{2,5})\s*[xX×]\s*(\d{2,5})")]
    private static partial Regex SizePattern();

    [GeneratedRegex(@"@\s*(-?\d+)\s*,\s*(-?\d+)")]
    private static partial Regex PositionPattern();
}
