// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Tript.App.Content;
using Tript.Core;
using Tript.Media;
using Tript.Settings;
using Xunit;
using Xunit.Sdk;

namespace Tript.App.Tests;

[Collection(AppHostCollection.Name)]
public sealed class ContentCatalogueTests : IDisposable
{
    private const string OverwatchId = "57ZZVAZ0PJK8VQGPKB728QE57C";

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

    [SkippableFact]
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

    [SkippableFact]
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

    [SkippableFact]
    public void MetadataRecord_LandsInMetadataTree_NotNextToTheVideo()
    {
        var root = _contentRoot;
        var store = new RecordingMetadataStore(Path.Combine(root, "metadata"));

        store.Save(new RecordingMetadata
        {
            VideoPath = "sessions/session-20260817-083000.mp4",
            Game = "Overwatch",
            Favorite = true,
            Bookmarks = { new Bookmark { Type = BookmarkType.Kill, Time = TimeSpan.FromSeconds(12) } },
        });

        var record = Path.Combine(root, "metadata", "session-20260817-083000.mp4.metadata.json");
        Assert.True(File.Exists(record), "the metadata record must live in metadata/, not next to the video");
        Assert.False(File.Exists(Path.Combine(root, "sessions", "session-20260817-083000.mp4.metadata.json")),
            "no metadata record may sit next to the .mp4");

        var loaded = store.Load("session-20260817-083000.mp4");
        Assert.NotNull(loaded);
        Assert.Equal("sessions/session-20260817-083000.mp4", loaded!.VideoPath);
        Assert.Equal("Overwatch", loaded.Game);
        Assert.True(loaded.Favorite);
        var bookmark = Assert.Single(loaded.Bookmarks);
        Assert.Equal(BookmarkType.Kill, bookmark.Type);
        Assert.Equal(TimeSpan.FromSeconds(12), bookmark.Time);
    }

    [SkippableFact]
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

    [SkippableFact]
    public void MetadataStore_SaveAndDelete_ReturnFalse_WhenTheWriteFails()
    {
        var root = _contentRoot;
        var fileAsDirectory = Path.Combine(root, "a-file");
        File.WriteAllText(fileAsDirectory, "in the way");

        var store = new RecordingMetadataStore(Path.Combine(fileAsDirectory, "metadata"));

        Assert.False(store.Save(new RecordingMetadata { VideoPath = "sessions/session-1.mp4" }),
            "Save must report a failed write instead of swallowing it");
        Assert.False(store.Delete("session-1.mp4"),
            "Delete must report a failed delete instead of swallowing it");
    }

    [SkippableFact]
    public async Task ListContent_ClassifiesSessionsAndClips_WithRelativePaths()
    {
        Directory.CreateDirectory(Path.Combine(_contentRoot, "sessions"));
        Directory.CreateDirectory(Path.Combine(_contentRoot, "clips"));
        await File.WriteAllTextAsync(Path.Combine(_contentRoot, "sessions", "session-1.mp4"), "session");
        await File.WriteAllTextAsync(Path.Combine(_contentRoot, "clips", "session-1-clip-x.mp4"), "clip");
        var clipTitles = new ClipTitleStore(Path.Combine(_contentRoot, "metadata"));
        Assert.True(clipTitles.SaveFavorite("session-1-clip-x.mp4", true));

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
        Assert.True(clip.GetProperty("favorite").GetBoolean());
        Assert.False(clip.TryGetProperty("bookmarks", out var _clipBookmarks), "clips never carry bookmarks");

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public async Task ListContent_ExposesAutomatedClipsWithTheirSourceSession()
    {
        Directory.CreateDirectory(Path.Combine(_contentRoot, "sessions"));
        Directory.CreateDirectory(Path.Combine(_contentRoot, "clips"));
        await File.WriteAllTextAsync(Path.Combine(_contentRoot, "sessions", "session-1.mp4"), "session");
        await File.WriteAllTextAsync(Path.Combine(_contentRoot, "clips", "session-1-highlight-1.mp4"), "clip");
        var clipTitles = new ClipTitleStore(Path.Combine(_contentRoot, "metadata"));
        Assert.True(clipTitles.SaveAutomatic("session-1-highlight-1.mp4", "sessions/session-1.mp4", 20, 40));

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"ListContent"}""");
        var (_, content) = await host.ReceiveAsyncParsed();
        var items = content.GetProperty("content").EnumerateArray().ToList();

        var highlight = items.Single(item => item.GetProperty("fileName").GetString() == "session-1-highlight-1.mp4");
        Assert.Equal("highlight", highlight.GetProperty("contentType").GetString());
        Assert.True(highlight.GetProperty("automated").GetBoolean());
        Assert.Equal("sessions/session-1.mp4", highlight.GetProperty("sourceSessionPath").GetString());
        Assert.Equal(20, highlight.GetProperty("clipStartTime").GetDouble());
        Assert.Equal(40, highlight.GetProperty("clipEndTime").GetDouble());

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public async Task ListContent_MissingAutomaticHighlightSource_EmitsFallbackRecording()
    {
        var highlights = Path.Combine(_contentRoot, "highlights");
        Directory.CreateDirectory(highlights);
        await File.WriteAllTextAsync(Path.Combine(highlights, "highlight-1.mp4"), "highlight");
        var clipTitles = new ClipTitleStore(Path.Combine(_contentRoot, "metadata"));
        Assert.True(clipTitles.SaveAutomatic("highlight-1.mp4", @"sessions\missing-session.mp4", 10, 20));

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"ListContent"}""");
        var (_, content) = await host.ReceiveAsyncParsed();
        var recording = content.GetProperty("content").EnumerateArray()
            .Single(item => item.GetProperty("contentType").GetString() == "recording");

        Assert.Equal("sessions/missing-session.mp4", recording.GetProperty("filePath").GetString());
        Assert.Equal("missing-session.mp4", recording.GetProperty("fileName").GetString());
        Assert.Equal("missing-session", recording.GetProperty("title").GetString());
        Assert.Equal("recording", recording.GetProperty("contentType").GetString());
        Assert.True(recording.GetProperty("videoMissing").GetBoolean());

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public async Task ListContent_MissingAutomaticHighlightSource_InheritsGameAndDateFromItsHighlights()
    {
        var highlights = Path.Combine(_contentRoot, "Overwatch", "highlights");
        Directory.CreateDirectory(highlights);
        var firstPath = Path.Combine(highlights, "highlight-1.mp4");
        var secondPath = Path.Combine(highlights, "highlight-2.mp4");
        await File.WriteAllTextAsync(firstPath, "highlight");
        await File.WriteAllTextAsync(secondPath, "highlight");
        var firstStart = new DateTime(2026, 8, 17, 15, 30, 0, DateTimeKind.Local);
        File.SetLastWriteTime(firstPath, firstStart);
        File.SetLastWriteTime(secondPath, firstStart.AddMinutes(2));

        var clipTitles = new ClipTitleStore(Path.Combine(_contentRoot, "metadata"));
        Assert.True(clipTitles.SaveAutomatic("highlight-1.mp4", "Overwatch/sessions/missing-session.mp4", 10, 20));
        Assert.True(clipTitles.SaveAutomatic("highlight-2.mp4", "Overwatch/sessions/missing-session.mp4", 30, 40));

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"ListContent"}""");
        var (_, content) = await host.ReceiveAsyncParsed();
        var recording = content.GetProperty("content").EnumerateArray()
            .Single(item => item.GetProperty("contentType").GetString() == "recording");

        Assert.Equal("Overwatch/sessions/missing-session.mp4", recording.GetProperty("filePath").GetString());
        Assert.Equal("Overwatch", recording.GetProperty("game").GetString());
        Assert.Equal(OverwatchId, recording.GetProperty("gameId").GetString());
        Assert.Equal(new DateTimeOffset(firstStart).ToUnixTimeSeconds(), recording.GetProperty("startTime").GetInt64());

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public async Task ListContent_MissingAutomaticHighlightSource_ProjectsRecordingMetadata()
    {
        var highlights = Path.Combine(_contentRoot, "highlights");
        Directory.CreateDirectory(highlights);
        await File.WriteAllTextAsync(Path.Combine(highlights, "highlight-1.mp4"), "highlight");
        var clipTitles = new ClipTitleStore(Path.Combine(_contentRoot, "metadata"));
        Assert.True(clipTitles.SaveAutomatic("highlight-1.mp4", "sessions/missing-session.mp4", 10, 20));

        var bookmarkId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var startTime = new DateTime(2026, 8, 17, 12, 30, 0, DateTimeKind.Local);
        var metadata = new RecordingMetadataStore(Path.Combine(_contentRoot, "metadata"));
        Assert.True(metadata.Save(new RecordingMetadata
        {
            VideoPath = "sessions/missing-session.mp4",
            Title = "Ranked comeback",
            Favorite = true,
            Game = "Stored game",
            GameId = "stored-game-id",
            StartTime = startTime,
            DurationSeconds = 137.5,
            AudioTracks =
            {
                new AudioTrackLayout { Index = 2, Name = "Discord" },
                new AudioTrackLayout { Index = 0, Name = "Game" },
            },
            Bookmarks =
            {
                new Bookmark
                {
                    Id = bookmarkId,
                    Type = BookmarkType.Kill,
                    Subtype = "headshot",
                    Time = TimeSpan.FromSeconds(12.5),
                },
            },
        }));

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"ListContent"}""");
        var (_, content) = await host.ReceiveAsyncParsed();
        var recording = content.GetProperty("content").EnumerateArray()
            .Single(item => item.GetProperty("contentType").GetString() == "recording");

        Assert.Equal("Ranked comeback", recording.GetProperty("title").GetString());
        Assert.True(recording.GetProperty("favorite").GetBoolean());
        Assert.Equal("Stored game", recording.GetProperty("game").GetString());
        Assert.Equal("stored-game-id", recording.GetProperty("gameId").GetString());
        Assert.Equal(new DateTimeOffset(startTime).ToUnixTimeSeconds(), recording.GetProperty("startTime").GetInt64());
        Assert.Equal(137.5, recording.GetProperty("durationSeconds").GetDouble());
        Assert.True(recording.GetProperty("hasAutomaticClipCandidates").GetBoolean());

        var tracks = recording.GetProperty("audioTracks").EnumerateArray().ToList();
        Assert.Equal(2, tracks.Count);
        Assert.Equal(0, tracks[0].GetProperty("index").GetInt32());
        Assert.Equal("Game", tracks[0].GetProperty("name").GetString());
        Assert.Equal(2, tracks[1].GetProperty("index").GetInt32());
        Assert.Equal("Discord", tracks[1].GetProperty("name").GetString());

        var bookmark = Assert.Single(recording.GetProperty("bookmarks").EnumerateArray());
        Assert.Equal(bookmarkId.ToString(), bookmark.GetProperty("id").GetString());
        Assert.Equal("kill", bookmark.GetProperty("type").GetString());
        Assert.Equal("headshot", bookmark.GetProperty("subtype").GetString());
        Assert.Equal(12.5, bookmark.GetProperty("time").GetDouble());

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public async Task ListContent_HighlightsOnlySession_EmitsIntentionalSessionContainer()
    {
        var highlights = Path.Combine(_contentRoot, "highlights");
        Directory.CreateDirectory(highlights);
        await File.WriteAllTextAsync(Path.Combine(highlights, "highlight-1.mp4"), "highlight");
        var clipTitles = new ClipTitleStore(Path.Combine(_contentRoot, "metadata"));
        Assert.True(clipTitles.SaveAutomatic("highlight-1.mp4", "sessions/buffer-session.mp4", 10, 20,
            sourceSessionHighlightsOnly: true));

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"ListContent"}""");
        var (_, content) = await host.ReceiveAsyncParsed();
        var session = content.GetProperty("content").EnumerateArray()
            .Single(item => item.GetProperty("contentType").GetString() == "recording");

        Assert.Equal("sessions/buffer-session.mp4", session.GetProperty("filePath").GetString());
        Assert.True(session.GetProperty("highlightsOnly").GetBoolean());
        Assert.False(session.TryGetProperty("videoMissing", out var videoMissing));

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public async Task ListContent_MixedSessionMarkers_RemainMissingVideo()
    {
        var highlights = Path.Combine(_contentRoot, "highlights");
        Directory.CreateDirectory(highlights);
        await File.WriteAllTextAsync(Path.Combine(highlights, "highlight-1.mp4"), "highlight");
        await File.WriteAllTextAsync(Path.Combine(highlights, "highlight-2.mp4"), "highlight");
        var clipTitles = new ClipTitleStore(Path.Combine(_contentRoot, "metadata"));
        Assert.True(clipTitles.SaveAutomatic("highlight-1.mp4", "sessions/session.mp4", 10, 20,
            sourceSessionHighlightsOnly: true));
        Assert.True(clipTitles.SaveAutomatic("highlight-2.mp4", "sessions/session.mp4", 30, 40));

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"ListContent"}""");
        var (_, content) = await host.ReceiveAsyncParsed();
        var session = content.GetProperty("content").EnumerateArray()
            .Single(item => item.GetProperty("contentType").GetString() == "recording");

        Assert.True(session.GetProperty("videoMissing").GetBoolean());
        Assert.False(session.TryGetProperty("highlightsOnly", out var highlightsOnly));

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public async Task ListContent_MultipleHighlightsForMissingSource_EmitOneRecording()
    {
        var highlights = Path.Combine(_contentRoot, "highlights");
        Directory.CreateDirectory(highlights);
        await File.WriteAllTextAsync(Path.Combine(highlights, "highlight-1.mp4"), "highlight");
        await File.WriteAllTextAsync(Path.Combine(highlights, "highlight-2.mp4"), "highlight");
        var clipTitles = new ClipTitleStore(Path.Combine(_contentRoot, "metadata"));
        Assert.True(clipTitles.SaveAutomatic("highlight-1.mp4", "sessions/missing-session.mp4", 10, 20));
        Assert.True(clipTitles.SaveAutomatic("highlight-2.mp4", @"sessions\missing-session.mp4", 30, 40));

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"ListContent"}""");
        var (_, content) = await host.ReceiveAsyncParsed();
        var recordings = content.GetProperty("content").EnumerateArray()
            .Where(item => item.GetProperty("contentType").GetString() == "recording").ToList();

        var recording = Assert.Single(recordings);
        Assert.Equal("sessions/missing-session.mp4", recording.GetProperty("filePath").GetString());

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public async Task ListContent_SourceOutsideRootOrContainingTraversal_DoesNotEmitPlaceholder()
    {
        var highlights = Path.Combine(_contentRoot, "highlights");
        Directory.CreateDirectory(highlights);
        await File.WriteAllTextAsync(Path.Combine(highlights, "outside.mp4"), "highlight");
        await File.WriteAllTextAsync(Path.Combine(highlights, "traversal.mp4"), "highlight");
        var clipTitles = new ClipTitleStore(Path.Combine(_contentRoot, "metadata"));
        Assert.True(clipTitles.SaveAutomatic("outside.mp4",
            Path.Combine(Path.GetDirectoryName(_contentRoot)!, "outside.mp4"), 10, 20));
        Assert.True(clipTitles.SaveAutomatic("traversal.mp4", "sessions/../outside.mp4", 10, 20));

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var hostScope = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"ListContent"}""");
        var (_, content) = await host.ReceiveAsyncParsed();
        var items = content.GetProperty("content").EnumerateArray().ToList();

        Assert.Equal(2, items.Count);
        Assert.DoesNotContain(items, item => item.GetProperty("contentType").GetString() == "recording");
        Assert.All(items, item => Assert.False(item.TryGetProperty("sourceSessionPath", out _)));

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public async Task ListContent_SourcePathComparison_FollowsPlatformCaseSensitivity()
    {
        var sessions = Path.Combine(_contentRoot, "sessions");
        var highlights = Path.Combine(_contentRoot, "highlights");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(highlights);
        await File.WriteAllTextAsync(Path.Combine(sessions, "session-1.mp4"), "session");
        await File.WriteAllTextAsync(Path.Combine(highlights, "highlight-1.mp4"), "highlight");
        var clipTitles = new ClipTitleStore(Path.Combine(_contentRoot, "metadata"));
        Assert.True(clipTitles.SaveAutomatic("highlight-1.mp4", "SESSIONS/SESSION-1.MP4", 10, 20));

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var hostScope = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"ListContent"}""");
        var (_, content) = await host.ReceiveAsyncParsed();
        var recordings = content.GetProperty("content").EnumerateArray()
            .Where(item => item.GetProperty("contentType").GetString() == "recording").ToList();

        Assert.Equal(OperatingSystem.IsWindows() ? 1 : 2, recordings.Count);
        if (OperatingSystem.IsWindows())
            Assert.DoesNotContain(recordings, item => item.TryGetProperty("videoMissing", out _));

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public async Task ListContent_SurvivingAutomaticHighlightSource_SuppressesPlaceholderAndVideoMissing()
    {
        var sessions = Path.Combine(_contentRoot, "sessions");
        var highlights = Path.Combine(_contentRoot, "highlights");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(highlights);
        await File.WriteAllTextAsync(Path.Combine(sessions, "session-1.mp4"), "session");
        await File.WriteAllTextAsync(Path.Combine(highlights, "highlight-1.mp4"), "highlight");
        var clipTitles = new ClipTitleStore(Path.Combine(_contentRoot, "metadata"));
        Assert.True(clipTitles.SaveAutomatic("highlight-1.mp4", "sessions/session-1.mp4", 10, 20));

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"ListContent"}""");
        var (_, content) = await host.ReceiveAsyncParsed();
        var recordings = content.GetProperty("content").EnumerateArray()
            .Where(item => item.GetProperty("contentType").GetString() == "recording").ToList();

        var recording = Assert.Single(recordings);
        Assert.Equal("sessions/session-1.mp4", recording.GetProperty("filePath").GetString());
        Assert.False(recording.TryGetProperty("videoMissing", out var _videoMissing));

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public async Task ListContent_IneligibleClipRecords_DoNotCreatePlaceholders()
    {
        var clips = Path.Combine(_contentRoot, "clips");
        Directory.CreateDirectory(clips);
        await File.WriteAllTextAsync(Path.Combine(clips, "manual.mp4"), "clip");
        await File.WriteAllTextAsync(Path.Combine(clips, "unlinked-automatic.mp4"), "clip");
        var clipTitles = new ClipTitleStore(Path.Combine(_contentRoot, "metadata"));
        Assert.True(clipTitles.SaveSourceSession("manual.mp4", "sessions/manual-source.mp4"));
        Assert.True(clipTitles.SaveAutomatic("unlinked-automatic.mp4", "   ", 10, 20));
        Assert.True(clipTitles.SaveAutomatic("missing-highlight.mp4", "sessions/orphan-source.mp4", 10, 20));

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"ListContent"}""");
        var (_, content) = await host.ReceiveAsyncParsed();
        var items = content.GetProperty("content").EnumerateArray().ToList();

        Assert.Equal(2, items.Count);
        Assert.DoesNotContain(items, item => item.GetProperty("contentType").GetString() == "recording");

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public async Task ListContent_RemovingLastLinkedHighlight_RemovesPlaceholder()
    {
        var highlights = Path.Combine(_contentRoot, "highlights");
        Directory.CreateDirectory(highlights);
        var highlightPath = Path.Combine(highlights, "highlight-1.mp4");
        await File.WriteAllTextAsync(highlightPath, "highlight");
        var clipTitles = new ClipTitleStore(Path.Combine(_contentRoot, "metadata"));
        Assert.True(clipTitles.SaveAutomatic("highlight-1.mp4", "sessions/missing-session.mp4", 10, 20));

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"ListContent"}""");
        var (_, before) = await host.ReceiveAsyncParsed();
        Assert.Contains(before.GetProperty("content").EnumerateArray(),
            item => item.TryGetProperty("videoMissing", out var missing) && missing.GetBoolean());

        File.Delete(highlightPath);
        await host.SendAsync("""{"method":"ListContent"}""");
        var (_, after) = await host.ReceiveAsyncParsed();
        Assert.Empty(after.GetProperty("content").EnumerateArray());

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public async Task ListContent_SessionWithMetadataShowsBookmarks_SessionWithoutShowsEmpty()
    {
        var sessions = Path.Combine(_contentRoot, "sessions");
        Directory.CreateDirectory(sessions);
        await File.WriteAllTextAsync(Path.Combine(sessions, "with-record.mp4"), "session");

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

        Assert.True(without.TryGetProperty("bookmarks", out var emptyBookmarks));
        Assert.Empty(emptyBookmarks.EnumerateArray());
        Assert.Equal("no-record", without.GetProperty("title").GetString());

        await host.ShutdownAsync();
    }

    private void WriteSourceWithBookmarks(string sessionFileName, params double[] bookmarkSeconds)
    {
        var sessions = Path.Combine(_contentRoot, "sessions");
        Directory.CreateDirectory(sessions);
        File.WriteAllText(Path.Combine(sessions, sessionFileName), "session");

        var metadata = new RecordingMetadata { VideoPath = $"sessions/{sessionFileName}" };
        foreach (var seconds in bookmarkSeconds)
        {
            metadata.Bookmarks.Add(new Bookmark
            {
                Type = BookmarkType.Kill,
                Time = TimeSpan.FromSeconds(seconds),
            });
        }

        Assert.True(new RecordingMetadataStore(Path.Combine(_contentRoot, "metadata")).Save(metadata));
    }

    private async Task<List<JsonElement>> ListContentItemsAsync()
    {
        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"ListContent"}""");
        var (_, content) = await host.ReceiveAsyncParsed();
        var items = content.GetProperty("content").EnumerateArray().ToList();

        await host.ShutdownAsync();
        return items;
    }

    private static List<double> BookmarkTimes(JsonElement item) =>
        item.TryGetProperty("bookmarks", out var bookmarks)
            ? bookmarks.EnumerateArray().Select(bookmark => bookmark.GetProperty("time").GetDouble()).ToList()
            : [];

    [SkippableFact]
    public async Task ListContent_HighlightInheritsItsSourceBookmarks_InClipLocalTime()
    {
        WriteSourceWithBookmarks("session-1.mp4", 120, 125);

        var highlights = Path.Combine(_contentRoot, "highlights");
        Directory.CreateDirectory(highlights);
        await File.WriteAllTextAsync(Path.Combine(highlights, "highlight-1.mp4"), "highlight");
        var clipTitles = new ClipTitleStore(Path.Combine(_contentRoot, "metadata"));
        Assert.True(clipTitles.SaveAutomatic("highlight-1.mp4", "sessions/session-1.mp4", 118, 133));

        var items = await ListContentItemsAsync();
        var highlight = items.Single(item => item.GetProperty("fileName").GetString() == "highlight-1.mp4");

        Assert.Equal([2, 7], BookmarkTimes(highlight));
    }

    [SkippableFact]
    public async Task ListContent_HighlightDoesNotInheritBookmarksOutsideItsRange()
    {
        WriteSourceWithBookmarks("session-1.mp4", 10, 120, 400);

        var highlights = Path.Combine(_contentRoot, "highlights");
        Directory.CreateDirectory(highlights);
        await File.WriteAllTextAsync(Path.Combine(highlights, "highlight-1.mp4"), "highlight");
        var clipTitles = new ClipTitleStore(Path.Combine(_contentRoot, "metadata"));
        Assert.True(clipTitles.SaveAutomatic("highlight-1.mp4", "sessions/session-1.mp4", 118, 133));

        var items = await ListContentItemsAsync();
        var highlight = items.Single(item => item.GetProperty("fileName").GetString() == "highlight-1.mp4");

        Assert.Equal([2], BookmarkTimes(highlight));
    }

    [SkippableFact]
    public async Task ListContent_DropsAnInheritedBookmarkBeyondTheClipsRealDuration()
    {
        WriteSourceWithBookmarks("session-1.mp4", 120, 130);

        var highlights = Path.Combine(_contentRoot, "highlights");
        Directory.CreateDirectory(highlights);
        await File.WriteAllTextAsync(Path.Combine(highlights, "highlight-1.mp4"), "highlight");
        var clipTitles = new ClipTitleStore(Path.Combine(_contentRoot, "metadata"));
        Assert.True(clipTitles.SaveAutomatic("highlight-1.mp4", "sessions/session-1.mp4", 118, 133));
        Assert.True(clipTitles.SaveDuration("highlight-1.mp4", 5));

        var items = await ListContentItemsAsync();
        var highlight = items.Single(item => item.GetProperty("fileName").GetString() == "highlight-1.mp4");

        Assert.Equal([2], BookmarkTimes(highlight));
    }

    [SkippableFact]
    public async Task ListContent_ManualClipWithoutSpans_InheritsNothing()
    {
        WriteSourceWithBookmarks("session-1.mp4", 120);

        var clips = Path.Combine(_contentRoot, "clips");
        Directory.CreateDirectory(clips);
        await File.WriteAllTextAsync(Path.Combine(clips, "manual.mp4"), "clip");
        var clipTitles = new ClipTitleStore(Path.Combine(_contentRoot, "metadata"));
        Assert.True(clipTitles.SaveSourceSession("manual.mp4", "sessions/session-1.mp4"));

        var items = await ListContentItemsAsync();
        var clip = items.Single(item => item.GetProperty("fileName").GetString() == "manual.mp4");

        Assert.Empty(BookmarkTimes(clip));
    }

    [SkippableFact]
    public async Task ListContent_MergedClipMapsEachBookmarkPastTheRemovedGap()
    {
        WriteSourceWithBookmarks("session-1.mp4", 15, 45);

        var clips = Path.Combine(_contentRoot, "clips");
        Directory.CreateDirectory(clips);
        await File.WriteAllTextAsync(Path.Combine(clips, "merged.mp4"), "clip");
        var clipTitles = new ClipTitleStore(Path.Combine(_contentRoot, "metadata"));
        Assert.True(clipTitles.SaveSourceSession("merged.mp4", "sessions/session-1.mp4",
        [
            new ClipSourceSpan { Start = 10, End = 20 },
            new ClipSourceSpan { Start = 40, End = 50 },
        ]));

        var items = await ListContentItemsAsync();
        var clip = items.Single(item => item.GetProperty("fileName").GetString() == "merged.mp4");

        Assert.Equal([5, 15], BookmarkTimes(clip));
    }

    [SkippableFact]
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

        await host.SendAsync("""{"method":"AddBookmark","parameters":{"filePath":"sessions/session-1.mp4","id":"","time":5,"type":"manual"}}""");
        await WaitUntil(() => File.Exists(metadataPath));

        Assert.True(File.Exists(metadataPath), "the bookmark must be stored in the metadata/ tree");
        Assert.False(File.Exists(Path.Combine(sessions, "session-1.mp4.bookmarks.json")),
            "no bookmark sidecar may sit next to the video");

        var record = JsonSerializer.Deserialize<RecordingMetadata>(File.ReadAllText(metadataPath),
            SettingsSerialization.Options)!;
        var bookmark = Assert.Single(record.Bookmarks);
        Assert.Equal(TimeSpan.FromSeconds(5), bookmark.Time);
        Assert.Equal(BookmarkType.Manual, bookmark.Type);
        Assert.Equal("sessions/session-1.mp4", record.VideoPath);

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

    [SkippableFact]
    public async Task AddBookmark_SaveFails_BroadcastsError()
    {
        var sessions = Path.Combine(_contentRoot, "sessions");
        Directory.CreateDirectory(sessions);
        await File.WriteAllTextAsync(Path.Combine(sessions, "session-1.mp4"), "session");

        File.WriteAllText(Path.Combine(_contentRoot, "metadata"), "in the way");

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"AddBookmark","parameters":{"filePath":"sessions/session-1.mp4","id":"","time":5,"type":"manual"}}""");

        var (method, content) = await host.ReceiveAsyncParsed();
        Assert.Equal("error", method);
        var message = content.GetProperty("message").GetString();
        Assert.NotNull(message);
        Assert.Contains("could not be saved", message, StringComparison.OrdinalIgnoreCase);

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public async Task DeleteBookmark_SaveFails_BroadcastsError()
    {
        var sessions = Path.Combine(_contentRoot, "sessions");
        Directory.CreateDirectory(sessions);
        await File.WriteAllTextAsync(Path.Combine(sessions, "session-1.mp4"), "session");

        var bookmarkId = new Guid("11111111-2222-3333-4444-555555555555");
        var recordPath = Path.Combine(_contentRoot, "metadata", "session-1.mp4.metadata.json");
        var store = new RecordingMetadataStore(Path.Combine(_contentRoot, "metadata"));
        store.Save(new RecordingMetadata
        {
            VideoPath = "sessions/session-1.mp4",
            Bookmarks =
            {
                new Bookmark { Id = bookmarkId, Type = BookmarkType.Manual, Time = TimeSpan.FromSeconds(5) },
            },
        });
        Assert.True(File.Exists(recordPath));
        File.SetAttributes(recordPath, FileAttributes.ReadOnly);

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync(
            """{"method":"DeleteBookmark","parameters":{"filePath":"sessions/session-1.mp4","id":"11111111-2222-3333-4444-555555555555"}}""");

        var (method, content) = await host.ReceiveAsyncParsed();
        Assert.Equal("error", method);
        var message = content.GetProperty("message").GetString();
        Assert.NotNull(message);
        Assert.Contains("could not be removed", message, StringComparison.OrdinalIgnoreCase);

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public async Task RenameContent_SaveFails_BroadcastsError_AndDoesNotPushContent()
    {
        var sessions = Path.Combine(_contentRoot, "sessions");
        Directory.CreateDirectory(sessions);
        await File.WriteAllTextAsync(Path.Combine(sessions, "session-1.mp4"), "session");

        File.WriteAllText(Path.Combine(_contentRoot, "metadata"), "in the way");

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync(
            """{"method":"RenameContent","parameters":{"fileName":"sessions/session-1.mp4","title":"Renamed session"}}""");

        var (method, content) = await host.ReceiveAsyncParsed();
        Assert.Equal("error", method);
        var message = content.GetProperty("message").GetString();
        Assert.NotNull(message);
        Assert.Contains("could not be saved", message, StringComparison.OrdinalIgnoreCase);

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public async Task RenameContent_StoresTheTitleInMetadata()
    {
        var sessions = Path.Combine(_contentRoot, "sessions");
        Directory.CreateDirectory(sessions);
        await File.WriteAllTextAsync(Path.Combine(sessions, "session-1.mp4"), "session");

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"RenameContent","parameters":{"fileName":"sessions/session-1.mp4","title":"Renamed session"}}""");
        var (method, _) = await host.ReceiveAsyncParsed();
        Assert.Equal("content", method);

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

    [SkippableFact]
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

        await host.SendAsync("""{"method":"DeleteContent","parameters":{"fileName":"sessions/session-1.mp4","contentType":"recording"}}""");
        var (method, _) = await host.ReceiveAsyncParsed();
        Assert.Equal("content", method);

        Assert.False(File.Exists(Path.Combine(sessions, "session-1.mp4")), "the video must be deleted");
        Assert.False(File.Exists(recordPath), "the metadata record must be deleted with its video");
        var stray = Directory.GetFiles(Path.Combine(_contentRoot, "metadata"), "*.metadata.json");
        Assert.Empty(stray);

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public async Task DeleteContent_RemovesTheMetadataRecord_WhenTheVideoIsAlreadyGone()
    {
        var sessions = Path.Combine(_contentRoot, "sessions");
        Directory.CreateDirectory(sessions);

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

        await host.SendAsync("""{"method":"DeleteContent","parameters":{"fileName":"sessions/session-gone.mp4","contentType":"recording"}}""");
        var (method, _) = await host.ReceiveAsyncParsed();
        Assert.Equal("content", method);

        Assert.False(File.Exists(recordPath), "the metadata record must be deleted even when the video is gone");
        var stray = Directory.GetFiles(Path.Combine(_contentRoot, "metadata"), "*.metadata.json");
        Assert.Empty(stray);

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public async Task DeleteContent_WithTheFrontendRootRelativePath_MovesTheVideo_DropsFromTheNextPush()
    {
        var nested = Path.Combine(_contentRoot, "sessions", "2026-08-01");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "session-1.mp4"), "session");

        var store = new RecordingMetadataStore(Path.Combine(_contentRoot, "metadata"));
        store.Save(new RecordingMetadata
        {
            VideoPath = "sessions/2026-08-01/session-1.mp4",
            Game = "Overwatch",
        });
        var recordPath = Path.Combine(_contentRoot, "metadata", "session-1.mp4.metadata.json");
        Assert.True(File.Exists(recordPath));

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync(
            """{"method":"DeleteContent","parameters":{"fileName":"sessions/2026-08-01/session-1.mp4","contentType":"recording"}}""");
        var (method, content) = await host.ReceiveAsyncParsed();
        Assert.Equal("content", method);

        Assert.False(File.Exists(Path.Combine(nested, "session-1.mp4")), "the video must leave the library");
        Assert.False(File.Exists(recordPath), "the metadata record must be moved with its video");

        var listed = new List<string>();
        foreach (var element in content.GetProperty("content").EnumerateArray())
            listed.Add(element.GetProperty("fileName").GetString() ?? string.Empty);
        Assert.False(listed.Contains("session-1.mp4"),
            "the deleted item must not be relisted on the content push that follows its delete");

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public async Task ClipTitle_RoundTripsThroughTheStore_AndDeletesWithTheClip()
    {
        var clips = Path.Combine(_contentRoot, "clips");
        Directory.CreateDirectory(clips);
        await File.WriteAllTextAsync(Path.Combine(clips, "session-1-clip-x.mp4"), "clip");

        var clipTitles = new ClipTitleStore(Path.Combine(_contentRoot, "metadata"));
        Assert.True(clipTitles.Save("session-1-clip-x.mp4", "The clutch"));

        var recordPath = Path.Combine(_contentRoot, "metadata", "session-1-clip-x.mp4.title.json");
        Assert.True(File.Exists(recordPath), "the clip title record must live in metadata/, not next to the video");
        Assert.False(File.Exists(Path.Combine(clips, "session-1-clip-x.mp4.title.json")),
            "no clip title record may sit next to the .mp4");

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"ListContent"}""");
        var (_, content) = await host.ReceiveAsyncParsed();

        var clip = content.GetProperty("content").EnumerateArray()
            .Single(i => i.GetProperty("contentType").GetString() == "clip");
        Assert.Equal("The clutch", clip.GetProperty("title").GetString());

        await host.SendAsync("""{"method":"DeleteContent","parameters":{"fileName":"clips/session-1-clip-x.mp4","contentType":"clip"}}""");
        var (method, _) = await host.ReceiveAsyncParsed();
        Assert.Equal("content", method);

        Assert.False(File.Exists(Path.Combine(clips, "session-1-clip-x.mp4")), "the clip must be deleted");
        Assert.False(File.Exists(recordPath), "the clip title record must be deleted with its clip");
        var stray = Directory.GetFiles(Path.Combine(_contentRoot, "metadata"), "*.title.json");
        Assert.Empty(stray);

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public async Task ListContent_ProjectsGameSizeAndDuration_FromTheMetadataRecord()
    {
        var sessions = Path.Combine(_contentRoot, "sessions");
        Directory.CreateDirectory(sessions);
        await File.WriteAllTextAsync(Path.Combine(sessions, "with-record.mp4"), new string('x', 4096));
        await File.WriteAllTextAsync(Path.Combine(sessions, "no-record.mp4"), "session");

        var store = new RecordingMetadataStore(Path.Combine(_contentRoot, "metadata"));
        store.Save(new RecordingMetadata
        {
            VideoPath = "sessions/with-record.mp4",
            Game = "Overwatch",
            StartTime = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Local),
            DurationSeconds = 137.5,
        });

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"ListContent"}""");
        var (_, content) = await host.ReceiveAsyncParsed();
        var items = content.GetProperty("content").EnumerateArray().ToList();

        var withRecord = items.Single(i => i.GetProperty("fileName").GetString() == "with-record.mp4");
        Assert.Equal("Overwatch", withRecord.GetProperty("game").GetString());
        Assert.Equal(137.5, withRecord.GetProperty("durationSeconds").GetDouble());
        Assert.Equal(4096, withRecord.GetProperty("fileSizeBytes").GetInt64());

        var without = items.Single(i => i.GetProperty("fileName").GetString() == "no-record.mp4");
        Assert.False(without.TryGetProperty("game", out var _noGame), "a recording with no record has no game");
        Assert.Equal(7, without.GetProperty("fileSizeBytes").GetInt64());
        Assert.True(without.GetProperty("startTime").GetDouble() > 0,
            "an item with no metadata record still carries a date");

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public async Task ListContent_ClipInheritsItsGame_FromTheSourceSessionName()
    {
        var sessions = Path.Combine(_contentRoot, "sessions");
        var clips = Path.Combine(_contentRoot, "clips");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(clips);
        await File.WriteAllTextAsync(Path.Combine(sessions, "session-1.mp4"), "session");
        await File.WriteAllTextAsync(Path.Combine(sessions, "session-10.mp4"), "session");

        await File.WriteAllTextAsync(Path.Combine(clips, "session-1-clip-k2m3xq.mp4"), "clip");
        await File.WriteAllTextAsync(Path.Combine(clips, "session-10-clip-1-0s-10s.mp4"), "clip");

        await File.WriteAllTextAsync(Path.Combine(clips, "session-99-clip-x.mp4"), "clip");
        await File.WriteAllTextAsync(Path.Combine(clips, "generated-name.mp4"), "clip");

        var store = new RecordingMetadataStore(Path.Combine(_contentRoot, "metadata"));
        store.Save(new RecordingMetadata { VideoPath = "sessions/session-1.mp4", Game = "Overwatch" });
        store.Save(new RecordingMetadata { VideoPath = "sessions/session-10.mp4", Game = "Deep Rock Galactic" });
        var clipTitles = new ClipTitleStore(Path.Combine(_contentRoot, "metadata"));
        Assert.True(clipTitles.SaveSourceSession("generated-name.mp4", "sessions/session-1.mp4"));

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"ListContent"}""");
        var (_, content) = await host.ReceiveAsyncParsed();
        var items = content.GetProperty("content").EnumerateArray().ToList();

        Assert.Equal("Overwatch", GameOf(items, "session-1-clip-k2m3xq.mp4"));

        Assert.Equal("Deep Rock Galactic", GameOf(items, "session-10-clip-1-0s-10s.mp4"));
        Assert.Null(GameOf(items, "session-99-clip-x.mp4"));
        Assert.Equal("Overwatch", GameOf(items, "generated-name.mp4"));

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public void ClipTitleStore_SaveGame_RoundTripsGameAndSurvivesSdrConversion()
    {
        var store = new ClipTitleStore(Path.Combine(_contentRoot, "metadata"));
        Assert.True(store.SaveAutomatic("session-1-highlight-a.mp4", "Overwatch/sessions/session-1.mp4", 10, 20));
        Assert.True(store.SaveGame("session-1-highlight-a.mp4", "Overwatch", "Overwatch"));

        var record = store.LoadRecord("session-1-highlight-a.mp4");
        Assert.Equal("Overwatch", record!.Game);
        Assert.Equal("Overwatch", record.GameId);

        Assert.True(store.SaveConvertedFrom("session-1-highlight-a.mp4", "session-1-highlight-a-sdr.mp4"));
        var converted = store.LoadRecord("session-1-highlight-a-sdr.mp4");
        Assert.Equal("Overwatch", converted!.Game);
        Assert.Equal("Overwatch", converted.GameId);
    }

    [SkippableFact]
    public async Task ListContent_ClipKeepsItsStoredGame_WhenTheSourceSessionIsGone()
    {
        var highlights = Path.Combine(_contentRoot, "highlights");
        Directory.CreateDirectory(highlights);
        await File.WriteAllTextAsync(Path.Combine(highlights, "session-1-highlight-a.mp4"), "clip");
        var clipTitles = new ClipTitleStore(Path.Combine(_contentRoot, "metadata"));
        Assert.True(clipTitles.SaveAutomatic("session-1-highlight-a.mp4", "sessions/session-1.mp4", 10, 20));
        Assert.True(clipTitles.SaveGame("session-1-highlight-a.mp4", "Overwatch", "Overwatch"));

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"ListContent"}""");
        var (_, content) = await host.ReceiveAsyncParsed();
        var items = content.GetProperty("content").EnumerateArray().ToList();

        Assert.Equal("Overwatch", GameOf(items, "session-1-highlight-a.mp4"));

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public async Task ListContent_BackfillsGameOntoAnOldHighlight_FromItsPerGamePath()
    {
        var highlights = Path.Combine(_contentRoot, "Overwatch", "highlights");
        Directory.CreateDirectory(highlights);
        await File.WriteAllTextAsync(Path.Combine(highlights, "session-1-highlight-live-abc.mp4"), "clip");
        var clipTitles = new ClipTitleStore(Path.Combine(_contentRoot, "metadata"));
        Assert.True(clipTitles.SaveAutomatic("session-1-highlight-live-abc.mp4",
            "Overwatch/sessions/session-1.mp4", 10, 20));

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"ListContent"}""");
        var (_, content) = await host.ReceiveAsyncParsed();
        var items = content.GetProperty("content").EnumerateArray().ToList();
        Assert.Equal("Overwatch", GameOf(items, "session-1-highlight-live-abc.mp4"));

        var record = new ClipTitleStore(Path.Combine(_contentRoot, "metadata"))
            .LoadRecord("session-1-highlight-live-abc.mp4");
        Assert.Equal("Overwatch", record!.Game);
        Assert.Equal(OverwatchId, record.GameId);

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public async Task ListContent_IsOrderedNewestFirst_Deterministically()
    {
        var sessions = Path.Combine(_contentRoot, "sessions");
        Directory.CreateDirectory(sessions);
        foreach (var name in new[] { "oldest.mp4", "middle.mp4", "newest.mp4", "tied-b.mp4", "tied-a.mp4" })
            await File.WriteAllTextAsync(Path.Combine(sessions, name), "session");

        var store = new RecordingMetadataStore(Path.Combine(_contentRoot, "metadata"));
        var baseTime = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Local);
        store.Save(new RecordingMetadata { VideoPath = "sessions/oldest.mp4", StartTime = baseTime });
        store.Save(new RecordingMetadata { VideoPath = "sessions/middle.mp4", StartTime = baseTime.AddHours(1) });
        store.Save(new RecordingMetadata { VideoPath = "sessions/newest.mp4", StartTime = baseTime.AddHours(2) });

        store.Save(new RecordingMetadata { VideoPath = "sessions/tied-a.mp4", StartTime = baseTime.AddMinutes(30) });
        store.Save(new RecordingMetadata { VideoPath = "sessions/tied-b.mp4", StartTime = baseTime.AddMinutes(30) });

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"ListContent"}""");
        var (_, first) = await host.ReceiveAsyncParsed();
        var order = first.GetProperty("content").EnumerateArray()
            .Select(i => i.GetProperty("fileName").GetString()).ToList();

        Assert.Equal(
            ["newest.mp4", "middle.mp4", "tied-a.mp4", "tied-b.mp4", "oldest.mp4"],
            order);

        await host.SendAsync("""{"method":"ListContent"}""");
        var (_, second) = await host.ReceiveAsyncParsed();
        Assert.Equal(order, second.GetProperty("content").EnumerateArray()
            .Select(i => i.GetProperty("fileName").GetString()).ToList());

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public async Task ListContent_ReadsTheDurationOnce_AndPersistsItOnTheRecord()
    {
        if (!TryLocateFfmpeg(out var ffmpeg, out var reason))
            throw new Xunit.SkipException(reason);

        var sessions = Path.Combine(_contentRoot, "sessions");
        Directory.CreateDirectory(sessions);
        GenerateTestVideo(ffmpeg, Path.Combine(sessions, "probeable.mp4"));

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"ListContent"}""");
        var (_, content) = await host.ReceiveAsyncParsed();
        var item = content.GetProperty("content").EnumerateArray()
            .Single(i => i.GetProperty("fileName").GetString() == "probeable.mp4");

        var duration = item.GetProperty("durationSeconds").GetDouble();
        Assert.InRange(duration, 1.5, 2.5);

        var recordPath = Path.Combine(_contentRoot, "metadata", "probeable.mp4.metadata.json");
        Assert.True(File.Exists(recordPath), "the duration must be persisted on the record");
        var record = JsonSerializer.Deserialize<RecordingMetadata>(await File.ReadAllTextAsync(recordPath),
            SettingsSerialization.Options)!;
        Assert.NotNull(record.DurationSeconds);
        Assert.Equal(duration, record.DurationSeconds!.Value, 3);
        Assert.Equal("sessions/probeable.mp4", record.VideoPath);

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public void MetadataStore_Read_TellsAnAbsentRecordFromAnUnreadableOne()
    {
        var metadataRoot = Path.Combine(_contentRoot, "metadata");
        var store = new RecordingMetadataStore(metadataRoot);

        Assert.Equal(StoredRecordState.Absent, store.Read("nothing-here.mp4").State);

        store.Save(new RecordingMetadata { VideoPath = "sessions/good.mp4", Game = "Overwatch" });
        var loaded = store.Read("good.mp4");
        Assert.Equal(StoredRecordState.Loaded, loaded.State);
        Assert.Equal("Overwatch", loaded.Record!.Game);
        Assert.False(loaded.MustNotBeOverwritten);

        Directory.CreateDirectory(metadataRoot);
        File.WriteAllText(Path.Combine(metadataRoot, "broken.mp4.metadata.json"), "{ this is not json");
        var unreadable = store.Read("broken.mp4");
        Assert.Equal(StoredRecordState.Unreadable, unreadable.State);
        Assert.Null(unreadable.Record);
        Assert.True(unreadable.MustNotBeOverwritten);
        Assert.False(string.IsNullOrWhiteSpace(unreadable.Failure), "the reason must be kept for the log line");

        File.WriteAllText(Path.Combine(metadataRoot, "nulled.mp4.metadata.json"), "null");
        Assert.Equal(StoredRecordState.Unreadable, store.Read("nulled.mp4").State);

        Assert.Null(store.Load("broken.mp4"));
    }

    [SkippableFact]
    public async Task ListContent_FlagsAutomaticClipCandidates_OnTheRecording()
    {
        var sessions = Path.Combine(_contentRoot, "sessions");
        Directory.CreateDirectory(sessions);
        await File.WriteAllTextAsync(Path.Combine(sessions, "candidates.mp4"), "recording");
        await File.WriteAllTextAsync(Path.Combine(sessions, "none.mp4"), "recording");

        var store = new RecordingMetadataStore(Path.Combine(_contentRoot, "metadata"));
        store.Save(new RecordingMetadata
        {
            VideoPath = "sessions/candidates.mp4",
            Bookmarks =
            {
                new Bookmark { Type = BookmarkType.Manual, Time = TimeSpan.FromSeconds(5) },

                new Bookmark { Type = BookmarkType.Kill, Time = TimeSpan.FromSeconds(10) },
            },
        });
        store.Save(new RecordingMetadata
        {
            VideoPath = "sessions/none.mp4",
            Bookmarks =
            {
                new Bookmark { Type = BookmarkType.Death, Time = TimeSpan.FromSeconds(20) },
                new Bookmark { Type = BookmarkType.Manual, Time = TimeSpan.FromSeconds(30) },
                new Bookmark { Type = BookmarkType.Kill, Time = TimeSpan.FromSeconds(40), IsAutomaticClipCandidate = false },
            },
        });

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var scope = host;
        await scope.ConnectWebSocketAsync();
        await DrainPushes(scope, 3);

        await scope.SendAsync("""{"method":"ListContent"}""");
        var (_, content) = await scope.ReceiveAsyncParsed();
        var items = content.GetProperty("content").EnumerateArray()
            .Select(item => (FileName: item.GetProperty("fileName").GetString(), HasCandidates: item.TryGetProperty("hasAutomaticClipCandidates", out var flag) && flag.GetBoolean()))
            .ToDictionary(pair => pair.FileName!, pair => pair.HasCandidates);

        Assert.True(items["candidates.mp4"], "a legacy Kill bookmark must count as a highlight candidate");
        Assert.False(items["none.mp4"], "an explicitly opted-out bookmark must not count");

        await scope.ShutdownAsync();
    }

    [SkippableFact]
    public async Task ListContent_ClipsCarryNoAutomaticClipCandidateFlag()
    {
        var clips = Path.Combine(_contentRoot, "clips");
        Directory.CreateDirectory(clips);
        await File.WriteAllTextAsync(Path.Combine(clips, "session-1-clip-x.mp4"), "clip");

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var scope = host;
        await scope.ConnectWebSocketAsync();
        await DrainPushes(scope, 3);

        await scope.SendAsync("""{"method":"ListContent"}""");
        var (_, content) = await scope.ReceiveAsyncParsed();
        var clip = content.GetProperty("content").EnumerateArray()
            .Single(item => item.GetProperty("contentType").GetString() == "clip");
        Assert.False(clip.TryGetProperty("hasAutomaticClipCandidates", out _),
            "clips must not carry the highlight-candidate flag");

        await scope.ShutdownAsync();
    }

    [SkippableFact]
    public async Task ListContent_LeavesAnUnreadableRecordUntouched_AndStillListsTheVideo()
    {
        if (!TryLocateFfmpeg(out var ffmpeg, out var reason))
            throw new Xunit.SkipException(reason);

        var sessions = Path.Combine(_contentRoot, "sessions");
        Directory.CreateDirectory(sessions);
        GenerateTestVideo(ffmpeg, Path.Combine(sessions, "probeable.mp4"));

        var metadataRoot = Path.Combine(_contentRoot, "metadata");
        Directory.CreateDirectory(metadataRoot);
        var recordPath = Path.Combine(metadataRoot, "probeable.mp4.metadata.json");
        await File.WriteAllTextAsync(recordPath, "{ \"game\": \"Overwatch\", this record is broken");
        var before = await File.ReadAllBytesAsync(recordPath);

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"ListContent"}""");
        var (_, content) = await host.ReceiveAsyncParsed();

        var item = content.GetProperty("content").EnumerateArray()
            .Single(i => i.GetProperty("fileName").GetString() == "probeable.mp4");
        Assert.Equal("probeable", item.GetProperty("title").GetString());

        Assert.Equal(before, await File.ReadAllBytesAsync(recordPath));

        Assert.Empty(Directory.GetFiles(metadataRoot, "*.tmp"));

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public async Task ListContent_PersistingADuration_KeepsTheGameTitleAndBookmarks()
    {
        if (!TryLocateFfmpeg(out var ffmpeg, out var reason))
            throw new Xunit.SkipException(reason);

        var sessions = Path.Combine(_contentRoot, "sessions");
        Directory.CreateDirectory(sessions);
        GenerateTestVideo(ffmpeg, Path.Combine(sessions, "probeable.mp4"));

        var store = new RecordingMetadataStore(Path.Combine(_contentRoot, "metadata"));
        Assert.True(store.Save(new RecordingMetadata
        {
            VideoPath = "sessions/probeable.mp4",
            Game = "Overwatch",
            Title = "Ranked win",
            StartTime = new DateTime(2026, 8, 17, 15, 20, 46, DateTimeKind.Local),
            Bookmarks =
            {
                new Bookmark { Type = BookmarkType.Kill, Time = TimeSpan.FromSeconds(12.5) },
                new Bookmark { Type = BookmarkType.Death, Time = TimeSpan.FromSeconds(34) },
            },
        }));

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"ListContent"}""");
        var (_, content) = await host.ReceiveAsyncParsed();
        var item = content.GetProperty("content").EnumerateArray()
            .Single(i => i.GetProperty("fileName").GetString() == "probeable.mp4");

        Assert.Equal("Overwatch", item.GetProperty("game").GetString());
        Assert.Equal("Ranked win", item.GetProperty("title").GetString());
        Assert.InRange(item.GetProperty("durationSeconds").GetDouble(), 1.5, 2.5);

        var recordPath = Path.Combine(_contentRoot, "metadata", "probeable.mp4.metadata.json");
        var record = JsonSerializer.Deserialize<RecordingMetadata>(await File.ReadAllTextAsync(recordPath),
            SettingsSerialization.Options)!;
        Assert.Equal("Overwatch", record.Game);
        Assert.Equal("Ranked win", record.Title);
        Assert.Equal(new DateTime(2026, 8, 17, 15, 20, 46, DateTimeKind.Local), record.StartTime);
        Assert.Equal(2, record.Bookmarks.Count);
        Assert.Equal(BookmarkType.Kill, record.Bookmarks[0].Type);
        Assert.NotNull(record.DurationSeconds);

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public async Task ListContent_HandWrittenRecordWithAnOffsetTimestamp_KeepsItsGameAndStartTime()
    {
        if (!TryLocateFfmpeg(out var ffmpeg, out var reason))
            throw new Xunit.SkipException(reason);

        var sessions = Path.Combine(_contentRoot, "sessions");
        Directory.CreateDirectory(sessions);
        GenerateTestVideo(ffmpeg, Path.Combine(sessions, "session-20260817-152046741.mp4"));

        var metadataRoot = Path.Combine(_contentRoot, "metadata");
        Directory.CreateDirectory(metadataRoot);
        var recordPath = Path.Combine(metadataRoot, "session-20260817-152046741.mp4.metadata.json");
        await File.WriteAllTextAsync(recordPath, """
            {
              "videoPath": "sessions/session-20260817-152046741.mp4",
              "game": "Overwatch",
              "contentType": "Recording",
              "startTime": "2026-08-17T15:20:46.7558115+02:00"
            }
            """);

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"ListContent"}""");
        var (_, content) = await host.ReceiveAsyncParsed();
        var item = content.GetProperty("content").EnumerateArray()
            .Single(i => i.GetProperty("fileName").GetString() == "session-20260817-152046741.mp4");

        var written = new DateTimeOffset(2026, 8, 17, 15, 20, 46, TimeSpan.FromHours(2))
            .AddTicks(7558115);
        Assert.Equal("Overwatch", item.GetProperty("game").GetString());
        Assert.Equal(written.ToUnixTimeSeconds(), item.GetProperty("startTime").GetInt64());

        var record = JsonSerializer.Deserialize<RecordingMetadata>(await File.ReadAllTextAsync(recordPath),
            SettingsSerialization.Options)!;
        Assert.Equal("Overwatch", record.Game);
        Assert.Equal(written, new DateTimeOffset(record.StartTime));
        Assert.NotNull(record.DurationSeconds);

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public void MetadataStore_ARecordBeingRewritten_IsNeverReadHalfWritten()
    {
        var store = new RecordingMetadataStore(Path.Combine(_contentRoot, "metadata"));
        var record = new RecordingMetadata
        {
            VideoPath = "sessions/hot.mp4",
            Game = "Overwatch",
            Title = "Ranked win",
            Bookmarks = { new Bookmark { Type = BookmarkType.Kill, Time = TimeSpan.FromSeconds(12) } },
        };
        Assert.True(store.Save(record));

        var stop = false;
        var writer = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
                store.Save(record);
        });
        writer.Start();

        var reads = 0;
        var unreadable = 0;
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
            while (DateTime.UtcNow < deadline)
            {
                reads++;
                var read = store.Read("hot.mp4");
                if (read.State != StoredRecordState.Loaded || read.Record!.Game != "Overwatch")
                    unreadable++;
            }
        }
        finally
        {
            Volatile.Write(ref stop, true);
            writer.Join();
        }

        Assert.True(reads > 100, $"the race needs to have actually run; only {reads} reads happened");
        Assert.Equal(0, unreadable);
    }

    [SkippableFact]
    public void MetadataStore_AReadOnlyRecord_IsNotRewritten()
    {
        var metadataRoot = Path.Combine(_contentRoot, "metadata");
        var store = new RecordingMetadataStore(metadataRoot);
        Assert.True(store.Save(new RecordingMetadata
        {
            VideoPath = "sessions/protected.mp4",
            Game = "Overwatch",
        }));

        var recordPath = Path.Combine(metadataRoot, "protected.mp4.metadata.json");
        File.SetAttributes(recordPath, FileAttributes.ReadOnly);
        var before = File.ReadAllBytes(recordPath);

        try
        {
            Assert.False(store.Save(new RecordingMetadata { VideoPath = "sessions/protected.mp4" }),
                "a read-only record must report a failed write, not be replaced");
            Assert.Equal(before, File.ReadAllBytes(recordPath));
            Assert.Empty(Directory.GetFiles(metadataRoot, "*.tmp"));
        }
        finally
        {
            File.SetAttributes(recordPath, FileAttributes.Normal);
        }
    }

    [SkippableFact]
    public void ClipStore_LeavesAnUnreadableRecordUntouched_AndReportsTheFailure()
    {
        var metadataRoot = Path.Combine(_contentRoot, "metadata");
        Directory.CreateDirectory(metadataRoot);
        var recordPath = Path.Combine(metadataRoot, "session-1-clip-x.mp4.title.json");
        File.WriteAllText(recordPath, "{ \"title\": \"The clutch\", broken");
        var before = File.ReadAllBytes(recordPath);

        var store = new ClipTitleStore(metadataRoot);
        Assert.Equal(StoredRecordState.Unreadable, store.Read("session-1-clip-x.mp4").State);

        Assert.False(store.SaveDuration("session-1-clip-x.mp4", 9.13),
            "a duration must not be written over a record that could not be read");
        Assert.False(store.Save("session-1-clip-x.mp4", "A new title"),
            "a title must not be written over a record that could not be read");
        Assert.Equal(before, File.ReadAllBytes(recordPath));

        Assert.True(store.SaveDuration("session-2-clip-y.mp4", 9.13));
        Assert.Equal(9.13, store.LoadRecord("session-2-clip-y.mp4")!.DurationSeconds);
    }

    private static string? GameOf(List<JsonElement> items, string fileName)
    {
        var item = items.Single(i => i.GetProperty("fileName").GetString() == fileName);
        return item.TryGetProperty("game", out var game) ? game.GetString() : null;
    }

    private static bool TryLocateFfmpeg(out string ffmpeg, out string reason)
    {
        try
        {
            (ffmpeg, _) = new FfmpegLocator().Locate();
            reason = string.Empty;
            return true;
        }
        catch (FfmpegNotFoundException exception)
        {
            ffmpeg = string.Empty;
            reason = $"A real duration needs ffprobe: {exception.Message}";
            return false;
        }
    }

    private static void GenerateTestVideo(string ffmpeg, string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
                 {
                     "-y", "-loglevel", "error",
                     "-f", "lavfi", "-i", "testsrc=duration=2:size=320x240:rate=30",
                     "-c:v", "libx264", "-pix_fmt", "yuv420p",
                     path,
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("ffmpeg could not be started to build the test source.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            process.WaitForExit(TimeSpan.FromSeconds(10));
            throw new Xunit.SkipException("ffmpeg did not finish generating the test source within 60 seconds.");
        }
        stdout.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0 || !File.Exists(path))
            throw new Xunit.SkipException($"The test source could not be generated by ffmpeg: {stderr.Trim()}");
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
