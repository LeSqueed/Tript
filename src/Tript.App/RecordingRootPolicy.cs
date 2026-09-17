// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Core;
using Tript.Settings;
using SettingsModel = Tript.Settings.Settings;

namespace Tript.App;

internal static class RecordingRootPolicy
{
    internal static string? WhyUnsafe(string candidate)
    {
        if (!Path.IsPathRooted(candidate))
            return "it is not an absolute path";

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                             or PathTooLongException)
        {
            return "it is not a usable path";
        }

        if (Path.GetPathRoot(full) is { } root &&
            string.Equals(Path.TrimEndingDirectorySeparator(root), full, StringComparison.Ordinal))
        {
            return "a filesystem root would expose the whole machine over the content server";
        }

        foreach (var folder in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            SettingsFilePaths.ConfigDirectory,
        })
        {
            if (string.IsNullOrEmpty(folder))
                continue;

            var sensitive = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (IsAtOrAbove(full, sensitive))
                return $"it contains '{sensitive}', which would expose it over the content server";
        }

        return null;
    }

    private static bool IsAtOrAbove(string candidate, string sensitive)
        => FilePaths.IsAtOrUnder(sensitive, candidate);

    internal static string Resolve(AppOptions options, SettingsModel settings)
    {
        var configured = settings.Recording.OutputDirectory;
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? options.ContentRoot : configured);
    }

    internal static string? Prepare(string root)
    {
        if (WhyUnsafe(root) is { } refusal)
            return $"the recording directory was refused because {refusal}.";

        Directory.CreateDirectory(root);
        return null;
    }
}
