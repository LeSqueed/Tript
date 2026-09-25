// SPDX-License-Identifier: GPL-2.0-or-later

using Tript.Core;

namespace Tript.GameDiscovery;

public static class PathSafety
{
    public static bool TryCanonicalize(IDiscoveryFileSystem fileSystem, string path, out string canonicalPath)
    {
        canonicalPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            canonicalPath = FilePaths.TrimTrailingSeparators(fileSystem.GetFullPath(path.Trim().Trim('"')));
            return Path.IsPathFullyQualified(canonicalPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static bool TryResolveLexicallyContained(
        IDiscoveryFileSystem fileSystem,
        string root,
        string candidate,
        out string fullPath)
    {
        fullPath = string.Empty;
        if (!TryCanonicalize(fileSystem, root, out var canonicalRoot) || string.IsNullOrWhiteSpace(candidate))
            return false;

        try
        {
            var combined = FilePaths.IsFullyQualified(candidate)
                ? candidate
                : Path.Combine(canonicalRoot, FilePaths.ToNativeSeparators(candidate));
            var canonicalCandidate = FilePaths.TrimTrailingSeparators(fileSystem.GetFullPath(combined));
            var prefix = canonicalRoot.EndsWith(Path.DirectorySeparatorChar)
                ? canonicalRoot
                : canonicalRoot + Path.DirectorySeparatorChar;
            if (!canonicalCandidate.StartsWith(prefix, FilePaths.Comparison))
                return false;

            fullPath = canonicalCandidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static bool TryResolveExistingDirectory(IDiscoveryFileSystem fileSystem, string candidate, out string resolved)
    {
        resolved = string.Empty;
        if (!TryCanonicalize(fileSystem, candidate, out var canonical) || !fileSystem.DirectoryExists(canonical))
            return false;

        try
        {
            resolved = FilePaths.TrimTrailingSeparators(fileSystem.ResolveLinks(canonical));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            resolved = canonical;
        }
        return true;
    }
}
