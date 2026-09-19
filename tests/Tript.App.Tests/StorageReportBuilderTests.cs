// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.App.Content;
using Xunit;

namespace Tript.App.Tests;

public sealed class StorageReportBuilderTests : IDisposable
{
    private readonly string _root;

    public StorageReportBuilderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tript-storage-report", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static ContentItem Item(string filePath, long bytes, string? game = null,
        string? gameId = null, bool favorite = false) =>
        new()
        {
            FilePath = filePath,
            FileName = filePath.Split('/')[^1],
            FileSizeBytes = bytes,
            Game = game,
            GameId = gameId,
            Favorite = favorite,
        };

    [Fact]
    public void TheSplit_FollowsTheFolderTheItemLivesIn()
    {
        var report = StorageReportBuilder.Build(_root,
        [
            Item("Overwatch/sessions/a.mp4", 100),
            Item("Overwatch/highlights/b.mp4", 20),
            Item("Overwatch/clips/c.mp4", 7),
        ], [], null);

        Assert.Equal(100, report.SessionBytes);
        Assert.Equal(20, report.HighlightBytes);
        Assert.Equal(7, report.ClipBytes);
        Assert.Equal(1, report.SessionCount);
        Assert.Equal(1, report.HighlightCount);
        Assert.Equal(1, report.ClipCount);
    }

    [Fact]
    public void TheTrashIsCounted_FromItsEntries()
    {
        var report = StorageReportBuilder.Build(_root, [],
        [
            new TrashEntry { Id = "one", FileSizeBytes = 40 },
            new TrashEntry { Id = "two", FileSizeBytes = 2 },
            new TrashEntry { Id = "three", FileSizeBytes = null },
        ], null);

        Assert.Equal(42, report.TrashBytes);
        Assert.Equal(3, report.TrashCount);
    }

    [Fact]
    public void EachGame_GetsItsOwnSplit_AndTheyAreOrderedBySize()
    {
        var report = StorageReportBuilder.Build(_root,
        [
            Item("Overwatch/sessions/a.mp4", 10, "Overwatch", "ow"),
            Item("Overwatch/highlights/b.mp4", 5, "Overwatch", "ow"),
            Item("Valorant/sessions/c.mp4", 100, "Valorant", "val"),
        ], [], null);

        Assert.Equal(["Valorant", "Overwatch"], report.Games.Select(game => game.Name));
        var overwatch = report.Games.Single(game => game.GameId == "ow");
        Assert.Equal(10, overwatch.SessionBytes);
        Assert.Equal(5, overwatch.HighlightBytes);
        Assert.Equal(15, overwatch.TotalBytes);
    }

    [Fact]
    public void ContentWithNoGame_IsGroupedOnItsOwn()
    {
        var report = StorageReportBuilder.Build(_root,
        [
            Item("sessions/a.mp4", 10),
        ], [], null);

        Assert.Null(Assert.Single(report.Games).Name);
    }

    [Fact]
    public void TheFileBeingRecorded_IsLeftOut()
    {
        var live = Item("Overwatch/sessions/live.mp4", 10, "Overwatch", "ow");
        live.Recording = true;

        var report = StorageReportBuilder.Build(_root, [live], [], null);

        Assert.Equal(0, report.SessionBytes);
        Assert.Empty(report.Games);
    }

    [Fact]
    public void FavouriteBytes_AreReportedSeparately_WithoutLeavingTheirCategory()
    {
        var report = StorageReportBuilder.Build(_root,
        [
            Item("Overwatch/sessions/a.mp4", 10, favorite: true),
            Item("Overwatch/sessions/b.mp4", 5),
        ], [], null);

        Assert.Equal(10, report.FavoriteBytes);
        Assert.Equal(15, report.SessionBytes);
    }

    [Fact]
    public void TheSidecarFolder_IsMeasuredOnDisk_AndCountedInTheLibraryTotal()
    {
        var metadata = ContentLayout.MetadataRoot(_root);
        Directory.CreateDirectory(Path.Combine(metadata, ContentLayout.Thumbnails));
        File.WriteAllBytes(Path.Combine(metadata, "a.mp4.metadata.json"), new byte[64]);
        File.WriteAllBytes(Path.Combine(metadata, ContentLayout.Thumbnails, "a.mp4.jpg"), new byte[36]);

        var report = StorageReportBuilder.Build(_root,
            [Item("Overwatch/sessions/a.mp4", 10)], [], null);

        Assert.Equal(100, report.SidecarBytes);
        Assert.Equal(110, report.LibraryBytes);
    }

    [Fact]
    public void TheVolumeFigures_ComeFromTheProbe()
    {
        var report = StorageReportBuilder.Build(_root, [], [],
            new VolumeSpace(@"T:\", 400, 500));

        Assert.Equal(@"T:\", report.VolumeRoot);
        Assert.Equal(400, report.VolumeFreeBytes);
        Assert.Equal(500, report.VolumeTotalBytes);
    }
}
