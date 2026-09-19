// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Core;

namespace Tript.App.Content;

internal static class ContentLayout
{
    internal const string Sessions = "sessions";
    internal const string Clips = "clips";
    internal const string Highlights = "highlights";
    internal const string Metadata = "metadata";

    internal const string Scratch = ".scratch";
    internal const string Thumbnails = "thumbnails";

    internal static string MetadataRoot(string root) => Path.Combine(root, Metadata);

    internal static string ThumbnailRoot(string root) => Path.Combine(root, Metadata, Thumbnails);

    internal static string TrashRoot(string root) => Path.Combine(root, TrashStore.DirectoryName);

    internal static string ScratchRoot(string root) => Path.Combine(root, Scratch);

    internal static bool IsReservedSegment(string segment) =>
        segment.Equals(TrashStore.DirectoryName, FilePaths.Comparison)
        || segment.Equals(Scratch, FilePaths.Comparison);

    internal static string ToWirePath(string root, string absolutePath) =>
        Path.GetRelativePath(root, absolutePath).Replace(Path.DirectorySeparatorChar, '/');

    internal static string FileNameOf(string wirePath)
    {
        var separator = wirePath.LastIndexOf('/');
        return separator >= 0 ? wirePath[(separator + 1)..] : wirePath;
    }

    internal static string TopLevelDirectory(string wirePath)
    {
        foreach (var segment in wirePath.Split('/'))
        {
            if (segment is Sessions or Clips or Highlights)
                return segment;
        }

        var separator = wirePath.IndexOf('/');
        return separator >= 0 ? wirePath[..separator] : wirePath;
    }

    internal static bool IsSessionPath(string wirePath) => TopLevelDirectory(wirePath) is Sessions;

    internal static bool IsClipPath(string wirePath) => TopLevelDirectory(wirePath) is Clips or Highlights;

    internal static bool IsTrashPath(string wirePath) =>
        wirePath.StartsWith(TrashStore.DirectoryName + "/", StringComparison.Ordinal);

    internal static string? GameSegment(string? wirePath)
    {
        if (string.IsNullOrWhiteSpace(wirePath))
            return null;

        var segment = wirePath.Split(['/', '\\'], 2)[0];
        return segment is Sessions or Clips or Highlights or Metadata or TrashStore.DirectoryName
            or Scratch
            ? null
            : segment;
    }

    internal static string SiblingOfSessions(string root, string sourcePath, string folder)
    {
        var parts = ToWirePath(root, sourcePath).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var sessionsIndex = Array.IndexOf(parts, Sessions);
        return sessionsIndex > 0
            ? Path.Combine([root, .. parts.Take(sessionsIndex), folder])
            : Path.Combine(root, folder);
    }
}
