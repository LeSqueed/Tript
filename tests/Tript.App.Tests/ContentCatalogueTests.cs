// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text;
using System.Text.Json;
using Tript.App.Content;
using Tript.Core;
using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

// The content catalogue and its metadata store. The library the frontend lists is rebuilt from the
// recording root on every push; this suite pins how the catalogue classifies sessions vs clips,
// where the metadata records live, and how user bookmarks and titles round-trip through the store
// rather than next to the video.
[Collection(AppHostCollection.Name)]
public sealed class ContentCatalogueTests : IDisposable
{
    private readonly AppHostCollectionFixture _fixture;
    private readonly string _contentRoot;
    private readonly string _settingsPath;

    public ContentCatalogueTests(AppHostCollectionFixture fixture)
    {
        _fixture = fixture;
        _contentRoot = fixture.NewContentRoot(nameof(ContentCatalogueTests));
        _settingsPath = fixture.NewSettingsPath(nameof(ContentCatalogueTests));
    }

    public void Dispose()
    {
    }

    // ---- clip naming ----

    [Fact]
    public void BuildClipOutputPath_Combine_UsesSourceBaseNameAndClipId_UnderClipsDir()
    {
        var parameters = new CreateClipParameters
        {
            FilePath = "sessions/session-20260817-083000.mp4",
            Id = "clip-k2m3xq",
            OutputMode = "combine",
        };

        var path = AppController.BuildClipOutputPath(parameters, _contentRoot);

        Assert.Equal(Path.Combine(_contentRoot, "clips", "session-20260817-083000-clip-k2m3xq.mp4"), path);
        Assert.True(Path.IsPathRooted(path), "the clip output path must be absolute, never CWD-relative");
        Assert.StartsWith(_contentRoot + Path.DirectorySeparatorChar, path);
    }

    [Fact]
    public void BuildClipOutputPath_Separate_IsTheClipsDirectory()
    {
        var parameters = new CreateClipParameters
        {
            FilePath = "sessions/session-20260817-083000.mp4",
            Id = "clip-k2m3xq",
            OutputMode = "separate",
        };

        var path = AppController.BuildClipOutputPath(parameters, _contentRoot);

        Assert.Equal(Path.Combine(_contentRoot, "clips"), path);
    }

    // ---- the store, directly ----

    [Fact]
    public void MetadataRecord_LandsInMetadataTree_NotNextToTheVideo()
    {
        var root = _contentRoot;
        var store = new RecordingMetadataStore(Path.Combine(root, "metadata"));

        store.Save(new RecordingMetadata
        {
            VideoPath = "sessions/session-20260817-083000.mp4",
            Game = "Overwatch",
            Bookmarks = { new Bookmark { Type = BookmarkType.Kill, Time = TimeSpan.FromSeconds(12) } },
        });

        // The record lives under <root>/metadata/, keyed by the video's file name.
        var record = Path.Combine(root, "metadata", "session-20260817-083000.mp4.metadata.json");
        Assert.True(File.Exists(record), "the metadata record must live in metadata/, not next to the video");
        Assert.False(File.Exists(Path.Combine(root, "sessions", "session-20260817-083000.mp4.metadata.json")),
            "no metadata record may sit next to the .mp4");

        var loaded = store.Load("session-20260817-083000.mp4");
        Assert.NotNull(loaded);
        Assert.Equal("sessions/session-20260817-083000.mp4", loaded!.VideoPath);
        Assert.Equal("Overwatch", loaded.Game);
        var bookmark = Assert.Single(loaded.Bookmarks);
        Assert.Equal(BookmarkType.Kill, bookmark.Type);
        Assert.Equal(TimeSpan.FromSeconds(12), bookmark.Time);
    }

    [Fact]
    public void MetadataStore_Delete_RemovesTheRecord()
    {
        var root = _contentRoot;
        var store = new RecordingMetadataStore(Path.Combine(root, "metadata"));
        store.Save(new RecordingMetadata { VideoPath = "sessions/session-1.mp4" });

        Assert.True(File.Exists(Path.Combine(root, "metadata", "session-1.mp4.metadata.json")));

        store.Delete("session-1.mp4");

        Assert.False(File.Exists(Path.Combine(root, "metadata", "session-1.mp4.metadata.json")),
            "deleting a video's metadata record must remove the record");
    }

    // ---- the catalogue, over the wire ----

    [Fact]
    public async Task ListContent_ClassifiesSessionsAndClips_WithRelativePaths()
    {
        Directory.CreateDirectory(Path.Combine(_contentRoot, "sessions"));
        Directory.CreateDirectory(Path.Combine(_contentRoot, "clips"));
        await File.WriteAllTextAsync(Path.Combine(_contentRoot, "sessions", "session-1.mp4"), "session");
        await File.WriteAllTextAsync(Path.Combine(_contentRoot, "clips", "session-1-clip-x.mp4"), "clip");

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"ListContent"}""");
        var (method, content) = await host.ReceiveAsyncParsed();
        Assert.Equal("content", method);

        var items = content.GetProperty("content").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);

        var session = items.Single(i => i.GetProperty("contentType").GetString() == "recording");
        Assert.Equal("sessions/session-1.mp4", session.GetProperty("filePath").GetString());
        Assert.Equal("session-1", session.GetProperty("title").GetString());

        var clip = items.Single(i => i.GetProperty("contentType").GetString() == "clip");
        Assert.Equal("clips/session-1-clip-x.mp4", clip.GetProperty("filePath").GetString());
        Assert.Equal("session-1-clip-x", clip.GetProperty("title").GetString());
        Assert.False(clip.TryGetProperty("bookmarks", out var _clipBookmarks), "clips never carry bookmarks");

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task ListContent_SessionWithMetadataShowsBookmarks_SessionWithoutShowsEmpty()
    {
        var sessions = Path.Combine(_contentRoot, "sessions");
        Directory.CreateDirectory(sessions);
        await File.WriteAllTextAsync(Path.Combine(sessions, "with-record.mp4"), "session");

        // A metadata record with a title, a start time and two bookmarks for one video.
        var store = new RecordingMetadataStore(Path.Combine(_contentRoot, "metadata"));
        store.Save(new RecordingMetadata
        {
            VideoPath = "sessions/with-record.mp4",
            Game = "Overwatch",
            StartTime = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Local),
            Title = "Ranked win",
            Bookmarks =
            {
                new Bookmark { Type = BookmarkType.Kill, Time = TimeSpan.FromSeconds(12.5) },
                new Bookmark { Type = BookmarkType.Death, Time = TimeSpan.FromSeconds(34) },
            },
        });

        // A second video with no record at all.
        await File.WriteAllTextAsync(Path.Combine(sessions, "no-record.mp4"), "session");

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"ListContent"}""");
        var (_, content) = await host.ReceiveAsyncParsed();

        var items = content.GetProperty("content").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);

        var withRecord = items.Single(i => i.GetProperty("fileName").GetString() == "with-record.mp4");
        Assert.Equal("Ranked win", withRecord.GetProperty("title").GetString());
        Assert.True(withRecord.TryGetProperty("startTime", out var startTime), "a session with a record carries its start time");
        Assert.True(startTime.GetDouble() > 0, "the start time is a unix seconds value");
        var bookmarks = withRecord.GetProperty("bookmarks").EnumerateArray().ToList();
        Assert.Equal(2, bookmarks.Count);
        Assert.Equal("kill", bookmarks[0].GetProperty("type").GetString());
        Assert.Equal(12.5, bookmarks[0].GetProperty("time").GetDouble());
        Assert.Equal("death", bookmarks[1].GetProperty("type").GetString());
        Assert.Equal(34, bookmarks[1].GetProperty("time").GetDouble());

        var without = items.Single(i => i.GetProperty("fileName").GetString() == "no-record.mp4");
        // A session with no metadata record still lists — empty bookmarks, no title field.
        Assert.True(without.TryGetProperty("bookmarks", out var emptyBookmarks));
        Assert.Empty(emptyBookmarks.EnumerateArray());
        Assert.Equal("no-record", without.GetProperty("title").GetString());

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task AddAndDeleteBookmark_RoundTripThroughTheMetadataStore()
    {
        var sessions = Path.Combine(_contentRoot, "sessions");
        Directory.CreateDirectory(sessions);
        await File.WriteAllTextAsync(Path.Combine(sessions, "session-1.mp4"), "session");

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        var metadataPath = Path.Combine(_contentRoot, "metadata", "session-1.mp4.metadata.json");

        // Add a user bookmark to a finished recording (bookmark changes do not push content, so
        // the record's appearance on disk is the completion signal).
        await host.SendAsync("""{"method":"AddBookmark","parameters":{"filePath":"sessions/session-1.mp4","id":"","time":5,"type":"manual"}}""");
        await WaitUntil(() => File.Exists(metadataPath));

        // The bookmark landed in the metadata store, not next to the video.
        Assert.True(File.Exists(metadataPath), "the bookmark must be stored in the metadata/ tree");
        Assert.False(File.Exists(Path.Combine(sessions, "session-1.mp4.bookmarks.json")),
            "no bookmark sidecar may sit next to the video");

        var record = JsonSerializer.Deserialize<RecordingMetadata>(File.ReadAllText(metadataPath),
            SettingsSerialization.Options)!;
        var bookmark = Assert.Single(record.Bookmarks);
        Assert.Equal(TimeSpan.FromSeconds(5), bookmark.Time);
        Assert.Equal(BookmarkType.Manual, bookmark.Type);
        Assert.Equal("sessions/session-1.mp4", record.VideoPath);

        // Delete it by id.
        var id = bookmark.Id.ToString();
        await host.SendAsync(
            $"{{\"method\":\"DeleteBookmark\",\"parameters\":{{\"filePath\":\"sessions/session-1.mp4\",\"id\":\"{id}\"}}}}");
        await WaitUntil(() =>
        {
            if (!File.Exists(metadataPath))
                return false;
            var loaded = JsonSerializer.Deserialize<RecordingMetadata>(File.ReadAllText(metadataPath),
                SettingsSerialization.Options)!;
            return loaded.Bookmarks.Count == 0;
        });

        var afterDelete = JsonSerializer.Deserialize<RecordingMetadata>(File.ReadAllText(metadataPath),
            SettingsSerialization.Options)!;
        Assert.Empty(afterDelete.Bookmarks);

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task RenameContent_StoresTheTitleInMetadata()
    {
        var sessions = Path.Combine(_contentRoot, "sessions");
        Directory.CreateDirectory(sessions);
        await File.WriteAllTextAsync(Path.Combine(sessions, "session-1.mp4"), "session");

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        // A rename broadcasts content; the push is the completion signal.
        await host.SendAsync("""{"method":"RenameContent","parameters":{"fileName":"sessions/session-1.mp4","title":"Renamed session"}}""");
        var (method, _) = await host.ReceiveAsyncParsed();
        Assert.Equal("content", method);

        // The title landed in the metadata store, not in a .title sidecar next to the video.
        var metadataPath = Path.Combine(_contentRoot, "metadata", "session-1.mp4.metadata.json");
        Assert.True(File.Exists(metadataPath));
        Assert.False(File.Exists(Path.Combine(sessions, "session-1.mp4.title")),
            "no .title sidecar may sit next to the video");

        var record = JsonSerializer.Deserialize<RecordingMetadata>(File.ReadAllText(metadataPath),
            SettingsSerialization.Options)!;
        Assert.Equal("Renamed session", record.Title);
        Assert.Equal("sessions/session-1.mp4", record.VideoPath);

        await host.ShutdownAsync();
    }

    // The cascade-delete contract: deleting a session removes both the .mp4 and its metadata
    // record, and leaves no stray record behind.
    [Fact]
    public async Task DeleteContent_RemovesTheVideoAndItsMetadataRecord()
    {
        var sessions = Path.Combine(_contentRoot, "sessions");
        Directory.CreateDirectory(sessions);
        await File.WriteAllTextAsync(Path.Combine(sessions, "session-1.mp4"), "session");

        var store = new RecordingMetadataStore(Path.Combine(_contentRoot, "metadata"));
        store.Save(new RecordingMetadata
        {
            VideoPath = "sessions/session-1.mp4",
            Bookmarks = { new Bookmark { Type = BookmarkType.Kill, Time = TimeSpan.FromSeconds(1) } },
        });
        var recordPath = Path.Combine(_contentRoot, "metadata", "session-1.mp4.metadata.json");
        Assert.True(File.Exists(recordPath));

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        // A delete broadcasts content; the push is the completion signal.
        await host.SendAsync("""{"method":"DeleteContent","parameters":{"fileName":"sessions/session-1.mp4","contentType":"recording"}}""");
        var (method, _) = await host.ReceiveAsyncParsed();
        Assert.Equal("content", method);

        Assert.False(File.Exists(Path.Combine(sessions, "session-1.mp4")), "the video must be deleted");
        Assert.False(File.Exists(recordPath), "the metadata record must be deleted with its video");
        var stray = Directory.GetFiles(Path.Combine(_contentRoot, "metadata"), "*.metadata.json");
        Assert.Empty(stray);

        await host.ShutdownAsync();
    }

    // The cascade-delete contract also holds when the video file was removed out-of-band: a delete
    // for a missing .mp4 must still drop the metadata record, so no orphan record accumulates.
    [Fact]
    public async Task DeleteContent_RemovesTheMetadataRecord_WhenTheVideoIsAlreadyGone()
    {
        var sessions = Path.Combine(_contentRoot, "sessions");
        Directory.CreateDirectory(sessions);
        // The .mp4 is deliberately absent: the video was deleted by hand (or a crash lost it).
        var store = new RecordingMetadataStore(Path.Combine(_contentRoot, "metadata"));
        store.Save(new RecordingMetadata
        {
            VideoPath = "sessions/session-gone.mp4",
            Bookmarks = { new Bookmark { Type = BookmarkType.Death, Time = TimeSpan.FromSeconds(2) } },
        });
        var recordPath = Path.Combine(_contentRoot, "metadata", "session-gone.mp4.metadata.json");
        Assert.True(File.Exists(recordPath));

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        // A delete broadcasts content even when there is no video on disk to remove.
        await host.SendAsync("""{"method":"DeleteContent","parameters":{"fileName":"sessions/session-gone.mp4","contentType":"recording"}}""");
        var (method, _) = await host.ReceiveAsyncParsed();
        Assert.Equal("content", method);

        Assert.False(File.Exists(recordPath), "the metadata record must be deleted even when the video is gone");
        var stray = Directory.GetFiles(Path.Combine(_contentRoot, "metadata"), "*.metadata.json");
        Assert.Empty(stray);

        await host.ShutdownAsync();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(25);
        }

        Assert.True(condition(), "the condition was not met within the timeout");
    }

    private static async Task DrainPushes(AppHostDriver host, int count)
    {
        for (var i = 0; i < count; i++)
            await host.ReceiveAsyncParsed();
    }
}
