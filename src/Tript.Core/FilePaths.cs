// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Core;

// Host path semantics in one place: case sensitivity follows the OS, while both separator styles
// are always understood because catalogue and settings data originate on Windows.
public static class FilePaths
{
    private static readonly char[] Separators = ['\\', '/'];

    public static StringComparer Comparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static StringComparison Comparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static string FileName(string path)
    {
        var separator = path.LastIndexOfAny(Separators);
        return separator >= 0 ? path[(separator + 1)..] : path;
    }

    public static bool ContainsSeparator(string path) => path.IndexOfAny(Separators) >= 0;

    public static string ToNativeSeparators(string path)
        => path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

    public static bool IsFullyQualified(string path)
        => Path.IsPathFullyQualified(path) || HasDriveRoot(path);

    public static string? TryGetFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    public static bool IsUnder(string path, string root)
    {
        var fullRoot = TryGetFullPath(root);
        var fullPath = TryGetFullPath(path);
        if (fullRoot is null || fullPath is null)
            return false;

        var prefix = WithTrailingSeparator(fullRoot);
        return fullPath.StartsWith(prefix, Comparison);
    }

    public static bool IsAtOrUnder(string path, string root)
    {
        var fullPath = TryGetFullPath(path);
        var fullRoot = TryGetFullPath(root);
        if (fullPath is null || fullRoot is null)
            return false;

        return Comparer.Equals(
            TrimTrailingSeparators(fullPath),
            TrimTrailingSeparators(fullRoot))
            || IsUnder(fullPath, fullRoot);
    }

    public static string CaseFold(string path) => OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;

    public static string TrimTrailingSeparators(string path)
    {
        var rootLength = Path.GetPathRoot(path)?.Length ?? 0;
        return path.Length > rootLength
            ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : path;
    }

    private static string WithTrailingSeparator(string path)
    {
        var trimmed = TrimTrailingSeparators(path);
        return trimmed.EndsWith(Path.DirectorySeparatorChar) ? trimmed : trimmed + Path.DirectorySeparatorChar;
    }

    private static bool HasDriveRoot(string path)
        => path.Length >= 3
            && char.IsAsciiLetter(path[0])
            && path[1] == ':'
            && (path[2] == '\\' || path[2] == '/');
}
