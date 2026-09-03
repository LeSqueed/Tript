// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Core;

// Game executables are Windows binaries on every host, so their names compare case-insensitively
// and with or without the .exe suffix regardless of the OS. Paths follow FilePaths instead.
public static class ExecutableNames
{
    private const string Extension = ".exe";

    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    public static StringComparison Comparison => StringComparison.OrdinalIgnoreCase;

    public static string Normalize(string? nameOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath))
            return string.Empty;

        var fileName = FilePaths.FileName(nameOrPath.Trim());
        return fileName.EndsWith(Extension, Comparison)
            ? fileName[..^Extension.Length]
            : fileName;
    }

    public static bool HasExeExtension(string path)
        => FilePaths.FileName(path).EndsWith(Extension, Comparison);

    public static bool Equal(string? left, string? right)
        => Comparer.Equals(Normalize(left), Normalize(right));
}
