// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Core;

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

    public static string ResolveLinks(string path)
    {
        const int MaximumLinkHops = 40;
        var full = Path.GetFullPath(path);
        var resolved = Path.GetPathRoot(full) ?? string.Empty;
        var pending = new Queue<string>(SplitSegments(full[resolved.Length..]));
        var hops = 0;
        while (pending.TryDequeue(out var segment))
        {
            var next = Path.Combine(resolved, segment);
            var target = new FileInfo(next).LinkTarget;
            if (target is null)
            {
                resolved = next;
                continue;
            }

            if (++hops > MaximumLinkHops)
                return full;

            var targetPath = Path.GetFullPath(target, resolved);
            resolved = Path.GetPathRoot(targetPath) ?? string.Empty;
            pending = new Queue<string>(SplitSegments(targetPath[resolved.Length..]).Concat(pending));
        }

        return TrimTrailingSeparators(resolved);
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

    private static string[] SplitSegments(string path)
        => path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

    private static bool HasDriveRoot(string path)
        => path.Length >= 3
            && char.IsAsciiLetter(path[0])
            && path[1] == ':'
            && (path[2] == '\\' || path[2] == '/');
}
