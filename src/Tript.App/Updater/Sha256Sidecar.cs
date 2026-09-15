// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.App.Updater;

// Parses sha256sum's "<hex>␠␠<filename>" format, as produced by release.yml's `sha256sum` step.
internal static class Sha256Sidecar
{
    internal static string? TryParse(string? sidecarContent)
    {
        if (string.IsNullOrWhiteSpace(sidecarContent))
            return null;

        var firstLine = sidecarContent.Split('\n', 2)[0].TrimEnd('\r');
        var hexToken = firstLine.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (hexToken is null || hexToken.Length != 64)
            return null;

        foreach (var character in hexToken)
        {
            if (!Uri.IsHexDigit(character))
                return null;
        }

        return hexToken.ToLowerInvariant();
    }
}
