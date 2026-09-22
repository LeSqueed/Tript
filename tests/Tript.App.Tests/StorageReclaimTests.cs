// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.App.Content;
using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

public sealed class StorageReclaimTests : IDisposable
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    private readonly string _root;
    private readonly SettingsStore _settings;
    private readonly AppHost _host;
    private readonly FakeStorageProbe _probe = new();

    public StorageReclaimTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tript-storage-reclaim", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var settingsPath = Path.Combine(_root, "settings.json");
        _settings = new SettingsStore(new SettingsFileProvider(settingsPath));
        _settings.Load().Storage.MinimumFreeBytes = 20 * Gigabyte;
        _settings.Save();

        _host = new AppHost(new AppOptions
        {
            ContentRoot = _root,
            SettingsPath = settingsPath,
            WebRoot = _root,
            FakeRecorder = true,
        }, _settings, runtime: null, new RecordingSessionTracker(), storageProbe: _probe);
    }

    public void Dispose()
    {
        _host.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Reclaim_NeverRemovesAFavourite_EvenWhenItIsTheOnlyThingLeft()
    {
        var favourite = WriteSession("Overwatch", "session-20260101-000000000.mp4", 4 * 1024 * 1024);
        Favourite(favourite);
        ShortBy(3 * 1024 * 1024);

        Reclaim();

        Assert.True(File.Exists(favourite));
    }

    [Fact]
    public void Reclaim_RemovesTheOldestSessionFirst_AndKeepsTheFavouriteOne()
    {
        var favourite = WriteSession("Overwatch", "session-20260101-000000000.mp4", 4 * 1024 * 1024);
        Favourite(favourite);
        var oldest = WriteSession("Overwatch", "session-20260102-000000000.mp4", 4 * 1024 * 1024);
        var newest = WriteSession("Overwatch", "session-20260103-000000000.mp4", 4 * 1024 * 1024);
        File.SetLastWriteTimeUtc(oldest, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newest, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc));
        ShortBy(3 * 1024 * 1024);

        Reclaim();

        Assert.True(File.Exists(favourite));
        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(newest));
    }

    [Fact]
    public void Reclaim_EmptiesTheTrashBeforeTouchingTheLibrary()
    {
        var session = WriteSession("Overwatch", "session-20260102-000000000.mp4", 4 * 1024 * 1024);
        var trashed = WriteSession("Overwatch", "session-20260101-000000000.mp4", 4 * 1024 * 1024);
        _host.DeleteContent(new DeleteContentParameters
        {
            ContentType = "recording",
            FileName = RelativePath(trashed),
        });
        Assert.False(File.Exists(trashed));
        Assert.NotEmpty(_host.TrashEntries());

        ShortBy(3 * 1024 * 1024);
        Reclaim();

        Assert.Empty(_host.TrashEntries());
        Assert.True(File.Exists(session));
    }

    [Fact]
    public void Reclaim_KeepsTheHighlightsCutFromASessionItRemoves()
    {
        var session = WriteSession("Overwatch", "session-20260101-000000000.mp4", 8 * 1024 * 1024);
        var highlight = WriteHighlight("Overwatch", "session-20260101-000000000-highlight-1.mp4",
            1 * 1024 * 1024, RelativePath(session));
        ShortBy(4 * 1024 * 1024);

        Reclaim();

        Assert.False(File.Exists(session));
        Assert.True(File.Exists(highlight));
    }

    [Fact]
    public void Reclaim_StopsAsSoonAsThereIsEnoughRoom()
    {
        var first = WriteSession("Overwatch", "session-20260101-000000000.mp4", 8 * 1024 * 1024);
        var second = WriteSession("Overwatch", "session-20260102-000000000.mp4", 8 * 1024 * 1024);
        File.SetLastWriteTimeUtc(first, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(second, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        ShortBy(4 * 1024 * 1024);

        Reclaim();

        Assert.False(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public void ADryRun_RemovesNothing()
    {
        var session = WriteSession("Overwatch", "session-20260101-000000000.mp4", 8 * 1024 * 1024);
        ShortBy(4 * 1024 * 1024);

        _host.ReclaimStorage(new ReclaimStorageParameters { DryRun = true });

        Assert.True(File.Exists(session));
    }

    [Fact]
    public void WithRoomToSpare_ReclaimRemovesNothing()
    {
        var session = WriteSession("Overwatch", "session-20260101-000000000.mp4", 8 * 1024 * 1024);
        _probe.Free = 400 * Gigabyte;

        Reclaim();

        Assert.True(File.Exists(session));
    }

    private void Reclaim() => _host.ReclaimStorage(new ReclaimStorageParameters());

    private void ShortBy(int bytes) => _probe.Free = (long)(20 * Gigabyte * 1.25) - bytes;

    private string RelativePath(string absolute) =>
        Path.GetRelativePath(_root, absolute).Replace(Path.DirectorySeparatorChar, '/');

    private string WriteSession(string game, string fileName, int bytes)
    {
        var directory = Path.Combine(_root, game, ContentLayout.Sessions);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, new byte[bytes]);

        var metadataRoot = ContentLayout.MetadataRoot(_root);
        Directory.CreateDirectory(metadataRoot);
        File.WriteAllText(Path.Combine(metadataRoot, $"{fileName}.metadata.json"),
            System.Text.Json.JsonSerializer.Serialize(new RecordingMetadata
            {
                VideoPath = RelativePath(path),
                Game = game,
                ContentType = ContentType.Recording,
            }, SettingsSerialization.Options));
        return path;
    }

    private string WriteHighlight(string game, string fileName, int bytes, string sourceSessionPath)
    {
        var directory = Path.Combine(_root, game, ContentLayout.Highlights);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, new byte[bytes]);

        var metadataRoot = ContentLayout.MetadataRoot(_root);
        Directory.CreateDirectory(metadataRoot);
        File.WriteAllText(Path.Combine(metadataRoot, $"{fileName}.title.json"),
            $$"""
            {"title":"{{Path.GetFileNameWithoutExtension(fileName)}}","isAutomatic":true,"sourceSessionPath":"{{sourceSessionPath}}"}
            """);
        return path;
    }

    private void Favourite(string absolute) =>
        _host.ToggleFavorite(new ToggleFavoriteParameters
        {
            ContentType = "recording",
            FilePath = RelativePath(absolute),
            Favorite = true,
        });

    private sealed class FakeStorageProbe : IStorageProbe
    {
        internal long Free { get; set; } = 400 * Gigabyte;

        internal long Total { get; set; } = 500 * Gigabyte;

        public VolumeSpace? Measure(string path) => new(@"T:\", Free, Total);
    }
}
