// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Serilog;

namespace Tript.App.Content;

internal static class StorageReportBuilder
{
    internal static StorageReport Build(string root, IReadOnlyList<ContentItem> items,
        IReadOnlyList<TrashEntry> trash, VolumeSpace? volume)
    {
        var report = new StorageReport
        {
            Root = root,
            VolumeRoot = volume?.RootPath,
            VolumeTotalBytes = volume?.TotalBytes ?? 0,
            VolumeFreeBytes = volume?.FreeBytes ?? 0,
            TrashBytes = trash.Sum(entry => entry.FileSizeBytes ?? 0),
            TrashCount = trash.Count,
        };

        var games = new Dictionary<string, StorageGameUsage>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            if (item.Recording == true)
                continue;

            var bytes = item.FileSizeBytes;
            var category = CategoryOf(item.FilePath);

            switch (category)
            {
                case ContentLayout.Sessions:
                    report.SessionBytes += bytes;
                    report.SessionCount++;
                    break;
                case ContentLayout.Highlights:
                    report.HighlightBytes += bytes;
                    report.HighlightCount++;
                    break;
                default:
                    report.ClipBytes += bytes;
                    report.ClipCount++;
                    break;
            }

            if (item.Favorite)
                report.FavoriteBytes += bytes;

            var key = item.GameId ?? item.Game ?? string.Empty;
            if (!games.TryGetValue(key, out var usage))
            {
                usage = new StorageGameUsage { GameId = item.GameId, Name = item.Game };
                games[key] = usage;
            }

            usage.TotalBytes += bytes;
            switch (category)
            {
                case ContentLayout.Sessions:
                    usage.SessionBytes += bytes;
                    break;
                case ContentLayout.Highlights:
                    usage.HighlightBytes += bytes;
                    break;
                default:
                    usage.ClipBytes += bytes;
                    break;
            }
        }

        report.SidecarBytes = DirectorySize(ContentLayout.MetadataRoot(root))
            + DirectorySize(ContentLayout.ScratchRoot(root));
        report.LibraryBytes = report.SessionBytes + report.HighlightBytes + report.ClipBytes
            + report.TrashBytes + report.SidecarBytes;

        report.Games = games.Values
            .OrderByDescending(usage => usage.TotalBytes)
            .ToList();

        return report;
    }

    private static string CategoryOf(string wirePath)
    {
        var top = ContentLayout.TopLevelDirectory(wirePath);
        return top is ContentLayout.Sessions or ContentLayout.Highlights or ContentLayout.Clips
            ? top
            : ContentLayout.Clips;
    }

    private static long DirectorySize(string directory)
    {
        if (!Directory.Exists(directory))
            return 0;

        var total = 0L;
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", enumeration))
                total += ContentCatalogue.SafeLength(new FileInfo(file));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warning("the size of {Directory} could not be measured: {Reason}", directory, exception.Message);
        }

        return total;
    }
}
