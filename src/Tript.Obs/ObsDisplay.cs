// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.RegularExpressions;

namespace Tript.Obs;

public sealed record ObsDisplay(string Id, string Name, int Index, int Width, int Height, bool Primary);

public readonly record struct ObsDisplayResolution(ObsDisplay? Selected, bool RequestedMissing);

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

        var name = text[..size.Index].TrimEnd(' ', '\t', ':', '(', '[', '-', '–', '\u2014');
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
