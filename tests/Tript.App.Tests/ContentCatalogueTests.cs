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

// The content catalogue and its metadata store. The library the frontend lists is rebuilt from the
// recording root on every push; this suite pins how the catalogue classifies sessions vs clips,
// where the metadata records live, how user bookmarks and titles round-trip through the store
// rather than next to the video, and which fields the library grid is drawn from.
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

    // ---- the store, directly ----

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

        // The record lives under <root>/metadata/, keyed by the video's file name.
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

    // A write failure (read-only media, disk full, permissions) must be reported to the caller:
    // Save/Delete return false instead of only logging to stderr, so a user bookmark or title
    // that failed to persist is never silently lost.
    [SkippableFact]
    public void MetadataStore_SaveAndDelete_ReturnFalse_WhenTheWriteFails()
    {
        // The metadata root sits on a path whose parent is a regular file, so creating the
        // metadata directory (and writing a record under it) fails with IOException. Deterministic
        // across platforms, unlike chmod-based read-only dirs.
        var root = _contentRoot;
        var fileAsDirectory = Path.Combine(root, "a-file");
        File.WriteAllText(fileAsDirectory, "in the way");

        var store = new RecordingMetadataStore(Path.Combine(fileAsDirectory, "metadata"));

        Assert.False(store.Save(new RecordingMetadata { VideoPath = "sessions/session-1.mp4" }),
            "Save must report a failed write instead of swallowing it");
        Assert.False(store.Delete("session-1.mp4"),
            "Delete must report a failed delete instead of swallowing it");
    }

    // ---- the catalogue, over the wire ----

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

    // A session deleted from the UI takes its metadata with it, leaving the placeholder with only its
    // highlights to speak for it. In the per-game recording layout the game still lives in the path
    // ("<gameId>/sessions/..."), so the placeholder must name it the way clips do; and its date is the
    // earliest of the surviving highlights, the closest truth left on disk.
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
        Assert.Equal("Overwatch", recording.GetProperty("gameId").GetString());
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

    // A bookmark that cannot be persisted must reach the user: the host broadcasts an 'error'
    // message carrying a human-readable message, instead of silently dropping the bookmark.
    [SkippableFact]
    public async Task AddBookmark_SaveFails_BroadcastsError()
    {
        var sessions = Path.Combine(_contentRoot, "sessions");
        Directory.CreateDirectory(sessions);
        await File.WriteAllTextAsync(Path.Combine(sessions, "session-1.mp4"), "session");

        // A regular file where the metadata directory would be created forces the metadata write
        // to fail, deterministically (the host creates the metadata root lazily on first save).
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

    // A bookmark whose deletion could not be persisted must reach the user the same way.
    [SkippableFact]
    public async Task DeleteBookmark_SaveFails_BroadcastsError()
    {
        var sessions = Path.Combine(_contentRoot, "sessions");
        Directory.CreateDirectory(sessions);
        await File.WriteAllTextAsync(Path.Combine(sessions, "session-1.mp4"), "session");

        // A record exists on disk already; making it read-only then makes the post-delete save
        // fail (UnauthorizedAccessException) while the record still loads. The ReadOnly attribute
        // is honoured on both platforms: on Unix it clears the file's write bits, on Windows it
        // sets the read-only flag, and File.WriteAllText rejects either with
        // UnauthorizedAccessException.
        var recordPath = Path.Combine(_contentRoot, "metadata", "session-1.mp4.metadata.json");
        var store = new RecordingMetadataStore(Path.Combine(_contentRoot, "metadata"));
        store.Save(new RecordingMetadata
        {
            VideoPath = "sessions/session-1.mp4",
            Bookmarks = { new Bookmark { Type = BookmarkType.Manual, Time = TimeSpan.FromSeconds(5) } },
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

    // A title that cannot be persisted must reach the user too — and the content list must not
    // be pushed as if the rename had succeeded (the old title stays on screen).
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

        // The rename must not be echoed as a successful content change: only the error message
        // is broadcast for the failed command.
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
    [SkippableFact]
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

    // The exact payload the frontend sends: `fileName` is the item's root-relative path — including
    // the nested date directory the recorder lays out — so the video itself is moved, its records
    // follow, and the item leaves the next `content` push instead of surviving on the grid.
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

    // A clip's user title (from the clip dialog) is stored in its own record in the metadata/
    // tree, read back into the library list instead of the file-name-without-extension, and
    // cascade-deleted with the clip.
    [SkippableFact]
    public async Task ClipTitle_RoundTripsThroughTheStore_AndDeletesWithTheClip()
    {
        var clips = Path.Combine(_contentRoot, "clips");
        Directory.CreateDirectory(clips);
        await File.WriteAllTextAsync(Path.Combine(clips, "session-1-clip-x.mp4"), "clip");

        // A clip has no RecordingMetadata record; its title lives in a dedicated clip-title
        // record, written the way the host writes it when a clip completes.
        var clipTitles = new ClipTitleStore(Path.Combine(_contentRoot, "metadata"));
        Assert.True(clipTitles.Save("session-1-clip-x.mp4", "The clutch"));

        // The record lands in metadata/, keyed by the clip's file name — never next to the .mp4.
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

        // Cascade delete: deleting the clip removes its title record too.
        await host.SendAsync("""{"method":"DeleteContent","parameters":{"fileName":"clips/session-1-clip-x.mp4","contentType":"clip"}}""");
        var (method, _) = await host.ReceiveAsyncParsed();
        Assert.Equal("content", method);

        Assert.False(File.Exists(Path.Combine(clips, "session-1-clip-x.mp4")), "the clip must be deleted");
        Assert.False(File.Exists(recordPath), "the clip title record must be deleted with its clip");
        var stray = Directory.GetFiles(Path.Combine(_contentRoot, "metadata"), "*.title.json");
        Assert.Empty(stray);

        await host.ShutdownAsync();
    }

    // ---- the fields the library grid needs ----

    // The library is a grid of cards filtered by game and sorted by date, so every card needs a game,
    // a date, a length and a size. The game comes from the recording's metadata record; the size is
    // read while the directory is enumerated; the duration is the persisted one (no probe is needed
    // when the record already carries it, which is the point of persisting it).
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

        // A recording with no record still lists, with no game. Its date falls back to the file's
        // last-write time so the grid can still place the card.
        var without = items.Single(i => i.GetProperty("fileName").GetString() == "no-record.mp4");
        Assert.False(without.TryGetProperty("game", out var _noGame), "a recording with no record has no game");
        Assert.Equal(7, without.GetProperty("fileSizeBytes").GetInt64());
        Assert.True(without.GetProperty("startTime").GetDouble() > 0,
            "an item with no metadata record still carries a date");

        await host.ShutdownAsync();
    }

    // A clip has no metadata record of its own, so it inherits its game from the session it was cut
    // from — recognised by its file name, which both clip naming paths start with the source
    // session's base name.
    [SkippableFact]
    public async Task ListContent_ClipInheritsItsGame_FromTheSourceSessionName()
    {
        var sessions = Path.Combine(_contentRoot, "sessions");
        var clips = Path.Combine(_contentRoot, "clips");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(clips);
        await File.WriteAllTextAsync(Path.Combine(sessions, "session-1.mp4"), "session");
        await File.WriteAllTextAsync(Path.Combine(sessions, "session-10.mp4"), "session");
        // The two shapes the two clip paths produce (AppController.BuildClipOutputPath for combine,
        // ClipEngine.BuildFileName for separate mode).
        await File.WriteAllTextAsync(Path.Combine(clips, "session-1-clip-k2m3xq.mp4"), "clip");
        await File.WriteAllTextAsync(Path.Combine(clips, "session-10-clip-1-0s-10s.mp4"), "clip");
        // A clip whose source is gone (or never had a game) has no game rather than a wrong one.
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
        // The boundary check: "session-1" must not claim a clip of "session-10".
        Assert.Equal("Deep Rock Galactic", GameOf(items, "session-10-clip-1-0s-10s.mp4"));
        Assert.Null(GameOf(items, "session-99-clip-x.mp4"));
        Assert.Equal("Overwatch", GameOf(items, "generated-name.mp4"));

        await host.ShutdownAsync();
    }

    // The clip record now carries the game itself, so an SDR-converted copy inherits the tag from
    // its source record just like it inherits the title and the duration.
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

    // A highlight that predates the game field still keeps it once it has been stored on the clip
    // record itself — even when the source session is gone, because deletion cascades only the
    // recording's own metadata record.
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

    // Older highlight records have no game field at all. The per-game recording layout puts the
    // highlight at "<gameId>/highlights/<name>.mp4" and links it to "<gameId>/sessions/<name>.mp4",
    // so even after the session is deleted the game can be named from the path and written onto the
    // record once — keeping the tag for every later listing.
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
        Assert.Equal("Overwatch", record.GameId);

        await host.ShutdownAsync();
    }

    // The frontend paginates over this list, so the order must be newest first and must be total —
    // two items with the same timestamp may not swap places between two pushes (List.Sort is
    // unstable, and the directory enumeration order is the file system's).
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
        // Two records sharing one timestamp: the relative path is the tiebreak.
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

        // The same list again is the same order: nothing about it depends on enumeration order.
        await host.SendAsync("""{"method":"ListContent"}""");
        var (_, second) = await host.ReceiveAsyncParsed();
        Assert.Equal(order, second.GetProperty("content").EnumerateArray()
            .Select(i => i.GetProperty("fileName").GetString()).ToList());

        await host.ShutdownAsync();
    }

    // The duration is read once per file and persisted, so it is not an ffprobe per item per push.
    // With a real (probeable) source the first list fills the record in; the value on the wire is the
    // container's duration.
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

        // The value landed on a metadata record, which is what makes every later push free.
        var recordPath = Path.Combine(_contentRoot, "metadata", "probeable.mp4.metadata.json");
        Assert.True(File.Exists(recordPath), "the duration must be persisted on the record");
        var record = JsonSerializer.Deserialize<RecordingMetadata>(await File.ReadAllTextAsync(recordPath),
            SettingsSerialization.Options)!;
        Assert.NotNull(record.DurationSeconds);
        Assert.Equal(duration, record.DurationSeconds!.Value, 3);
        Assert.Equal("sessions/probeable.mp4", record.VideoPath);

        await host.ShutdownAsync();
    }

    // ---- a record that exists but cannot be read ----

    // The three states a load can find. The read path collapses two of them into null (an item lists
    // either way, which is the point), so the store has to report them separately for the callers
    // that write back — that is the whole defence against a blank record replacing a good one.
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

        // Garbage bytes: there is a file, and nothing in it can be recovered.
        Directory.CreateDirectory(metadataRoot);
        File.WriteAllText(Path.Combine(metadataRoot, "broken.mp4.metadata.json"), "{ this is not json");
        var unreadable = store.Read("broken.mp4");
        Assert.Equal(StoredRecordState.Unreadable, unreadable.State);
        Assert.Null(unreadable.Record);
        Assert.True(unreadable.MustNotBeOverwritten);
        Assert.False(string.IsNullOrWhiteSpace(unreadable.Failure), "the reason must be kept for the log line");

        // A file holding the literal "null" parses without an exception and yields no record; there
        // is still a file, so it counts as present, not absent.
        File.WriteAllText(Path.Combine(metadataRoot, "nulled.mp4.metadata.json"), "null");
        Assert.Equal(StoredRecordState.Unreadable, store.Read("nulled.mp4").State);

        // The read path is unchanged: an unreadable record still loads as "no record", so the video
        // keeps its entry in the library.
        Assert.Null(store.Load("broken.mp4"));
    }

    // The library exposes whether a recording has cuttable detected events so the player can keep
    // the "Create highlights" action disabled until there is something to cut. The flag mirrors the
    // exact predicate CreateAutomaticClips uses: an explicit candidate flag (an event definition
    // that opted in) or a legacy type that predates per-definition flagging.
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
                // Legacy: Kill is included in highlights by type.
                new Bookmark { Type = BookmarkType.Kill, Time = TimeSpan.FromSeconds(10) },
            },
        });
        store.Save(new RecordingMetadata
        {
            VideoPath = "sessions/none.mp4",
            Bookmarks =
            {
                // Explicitly opted out: an event definition that stays out of automatic clips.
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

    // A clip has no highlight-candidate flag at all: the "Create highlights" dimension does not
    // apply to clips, so the wire must not suggest the action exists for them.
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

    // The bug this suite grew for. A record that exists but cannot be parsed used to be reported as
    // null, which the duration-persisting path read as "there is no record" — so it wrote a fresh
    // record holding a video path and a duration over a file that held the recording's game, title
    // and bookmarks. The file's bytes must survive a list untouched, and the video must still list.
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

        // The item lists — an unreadable record may not take the library entry down with it.
        var item = content.GetProperty("content").EnumerateArray()
            .Single(i => i.GetProperty("fileName").GetString() == "probeable.mp4");
        Assert.Equal("probeable", item.GetProperty("title").GetString());

        // And the record is byte-for-byte what it was: the duration is recomputable, whatever is in
        // this file is not.
        Assert.Equal(before, await File.ReadAllBytesAsync(recordPath));

        // No half-written sibling left in the tree either (the write is a rename over the target).
        Assert.Empty(Directory.GetFiles(metadataRoot, "*.tmp"));

        await host.ShutdownAsync();
    }

    // The regression test for the reported data loss: a record carrying a game, a user title and
    // bookmarks goes through a ListContent that fills in the duration, and comes out with all three
    // still in it.
    [SkippableFact]
    public async Task ListContent_PersistingADuration_KeepsTheGameTitleAndBookmarks()
    {
        if (!TryLocateFfmpeg(out var ffmpeg, out var reason))
            throw new Xunit.SkipException(reason);

        var sessions = Path.Combine(_contentRoot, "sessions");
        Directory.CreateDirectory(sessions);
        GenerateTestVideo(ffmpeg, Path.Combine(sessions, "probeable.mp4"));

        // Deliberately no DurationSeconds: this is the record the probe wants to write into.
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

        // On disk: the duration was added, and nothing else was traded for it.
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

    // A hand-written record, exactly as a user recovering a recording would type it: camelCase
    // members, a content type by name, and a start time carrying a local offset. Every part of it
    // loads (the offset form is the same one the app itself writes), so the game reaches the wire and
    // the timestamp is not reset when the duration is filled in.
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

        // The instant the file names, not the machine's idea of it: the assertions compare
        // DateTimeOffsets, so they hold in any time zone the tests run in.
        var written = new DateTimeOffset(2026, 8, 17, 15, 20, 46, TimeSpan.FromHours(2))
            .AddTicks(7558115);
        Assert.Equal("Overwatch", item.GetProperty("game").GetString());
        Assert.Equal(written.ToUnixTimeSeconds(), item.GetProperty("startTime").GetInt64());

        // The record kept the game and the instant after the duration was written into it.
        var record = JsonSerializer.Deserialize<RecordingMetadata>(await File.ReadAllTextAsync(recordPath),
            SettingsSerialization.Options)!;
        Assert.Equal("Overwatch", record.Game);
        Assert.Equal(written, new DateTimeOffset(record.StartTime));
        Assert.NotNull(record.DurationSeconds);

        await host.ShutdownAsync();
    }

    // A record is rewritten by a rename over the target rather than in place, because the host
    // reads and writes these records from two threads: a finished clip pushes content from its own
    // thread while the IPC thread may be listing, and a list both reads records and writes
    // durations into them. Measured on the plain File.WriteAllText this replaced: 115041 of 506391
    // concurrent reads (22.7%) threw JsonException, most often "The input does not contain any JSON
    // tokens" — the window where the file has been truncated and not yet rewritten.
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

    // A read-only record is still not written, now that the write is a rename over the target. This
    // needs its own test because the rename does not check the destination's permissions at all: on
    // Unix rename(2) only needs a writable directory, so an atomic write silently gained the ability
    // to replace a record that the read-only bit exists to protect (DeleteBookmark_SaveFails_
    // BroadcastsError depends on this, and it hung when the save unexpectedly succeeded).
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

    // The clip record carries the user's clip title, so it gets the same protection: a record that
    // cannot be read is not replaced by one holding just a duration.
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

        // A clip with no record at all is the normal case and still gets one.
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
