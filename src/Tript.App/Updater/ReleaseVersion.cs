// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Globalization;

namespace Tript.App.Updater;

// Not GameModelManager's numeric comparison: releases differ only by prerelease suffix.
internal readonly struct ReleaseVersion : IComparable<ReleaseVersion>
{
    private static readonly string[] PrereleaseLabelOrder = ["alpha", "beta"];

    private readonly int _major;
    private readonly int _minor;
    private readonly int _patch;
    private readonly string? _prereleaseLabel;
    private readonly int _prereleaseNumber;

    private ReleaseVersion(int major, int minor, int patch, string? prereleaseLabel, int prereleaseNumber)
    {
        _major = major;
        _minor = minor;
        _patch = patch;
        _prereleaseLabel = prereleaseLabel;
        _prereleaseNumber = prereleaseNumber;
    }

    internal static bool TryParse(string? raw, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var text = raw.Trim();
        if (text.Length > 0 && (text[0] == 'v' || text[0] == 'V'))
            text = text[1..];

        var plusIndex = text.IndexOf('+', StringComparison.Ordinal);
        if (plusIndex >= 0)
            text = text[..plusIndex];

        string corePart;
        string? prereleasePart;
        var dashIndex = text.IndexOf('-', StringComparison.Ordinal);
        if (dashIndex < 0)
        {
            corePart = text;
            prereleasePart = null;
        }
        else
        {
            corePart = text[..dashIndex];
            prereleasePart = text[(dashIndex + 1)..];
        }

        var coreParts = corePart.Split('.');
        if (coreParts.Length != 3
            || !int.TryParse(coreParts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(coreParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(coreParts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        string? label = null;
        var number = 0;
        if (prereleasePart is not null)
        {
            var prereleaseParts = prereleasePart.Split('.');
            if (prereleaseParts.Length != 2 || prereleaseParts[0].Length == 0
                || !prereleaseParts[0].All(char.IsAsciiLetter)
                || !int.TryParse(prereleaseParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out number))
            {
                return false;
            }

            label = prereleaseParts[0].ToLowerInvariant();
        }

        version = new ReleaseVersion(major, minor, patch, label, number);
        return true;
    }

    internal static bool IsNewer(string candidateRaw, string currentRaw) =>
        TryParse(candidateRaw, out var candidate) && TryParse(currentRaw, out var current)
            && candidate.CompareTo(current) > 0;

    public int CompareTo(ReleaseVersion other)
    {
        var comparison = _major.CompareTo(other._major);
        if (comparison != 0)
            return comparison;
        comparison = _minor.CompareTo(other._minor);
        if (comparison != 0)
            return comparison;
        comparison = _patch.CompareTo(other._patch);
        if (comparison != 0)
            return comparison;

        if (_prereleaseLabel is null && other._prereleaseLabel is null)
            return 0;
        if (_prereleaseLabel is null)
            return 1;
        if (other._prereleaseLabel is null)
            return -1;

        comparison = PrereleaseLabelRank(_prereleaseLabel).CompareTo(PrereleaseLabelRank(other._prereleaseLabel));
        if (comparison != 0)
            return comparison;
        comparison = string.CompareOrdinal(_prereleaseLabel, other._prereleaseLabel);
        return comparison != 0 ? comparison : _prereleaseNumber.CompareTo(other._prereleaseNumber);
    }

    private static int PrereleaseLabelRank(string label)
    {
        var index = Array.IndexOf(PrereleaseLabelOrder, label);
        return index < 0 ? PrereleaseLabelOrder.Length : index;
    }

    public override string ToString()
    {
        var core = $"{_major}.{_minor}.{_patch}";
        return _prereleaseLabel is null ? core : $"{core}-{_prereleaseLabel}.{_prereleaseNumber}";
    }
}
