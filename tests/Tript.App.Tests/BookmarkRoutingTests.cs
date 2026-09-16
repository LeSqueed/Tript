// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Reflection;
using System.Text.Json;
using Tript.Core;
using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

public sealed class BookmarkRoutingTests : IDisposable
{
    private readonly string _root;
    private readonly SettingsStore _store;
    private readonly RecordingSessionTracker _tracker;
    private readonly AppHost _host;

    public BookmarkRoutingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tript-bookmark-routing", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "sessions"));
        File.WriteAllText(Path.Combine(_root, "sessions", "old.mp4"), "old");

        var settingsPath = Path.Combine(_root, "settings.json");
        _store = new SettingsStore(new SettingsFileProvider(settingsPath));
        _store.Save();

        _tracker = new RecordingSessionTracker();
        _host = new AppHost(new AppOptions
        {
            ContentRoot = _root,
            SettingsPath = settingsPath,
            WebRoot = _root,
            FakeRecorder = true,
        }, _store, runtime: null, _tracker, recorderStopTimeout: TimeSpan.FromMilliseconds(50));
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

    private string ActiveSessionRelativePath()
    {
        var absolute = (string?)typeof(AppHost)
            .GetField("_activeSessionPath", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(_host);
        Assert.NotNull(absolute);
        return Path.GetRelativePath(_root, absolute).Replace(Path.DirectorySeparatorChar, '/');
    }

    private List<Bookmark> StoredBookmarks(string fileName)
    {
        var path = Path.Combine(_root, "metadata", $"{fileName}.metadata.json");
        if (!File.Exists(path))
        {
            return [];
        }
        var record = JsonSerializer.Deserialize<RecordingMetadata>(File.ReadAllText(path),
            SettingsSerialization.Options)!;
        return record.Bookmarks;
    }

    [Fact]
    public void AddBookmark_WhileRecordingAnotherFile_WritesToTheNamedRecording()
    {
        Assert.True(_host.StartRecording(gameId: null));

        _host.AddBookmark(new AddBookmarkParameters
        {
            FilePath = "sessions/old.mp4",
            Time = 5,
            Type = "manual",
        });

        var stored = Assert.Single(StoredBookmarks("old.mp4"));
        Assert.Equal(TimeSpan.FromSeconds(5), stored.Time);
        Assert.Equal(BookmarkType.Manual, stored.Type);
        Assert.Empty(_tracker.Active!.Bookmarks);

        Assert.True(_host.StopRecording());
    }

    [Fact]
    public void AddBookmark_ForTheActiveRecording_GoesToTheLiveSession()
    {
        Assert.True(_host.StartRecording(gameId: null));
        var active = ActiveSessionRelativePath();

        _host.AddBookmark(new AddBookmarkParameters
        {
            FilePath = active,
            Time = 12,
            Type = "manual",
        });

        var live = Assert.Single(_tracker.Active!.Bookmarks);
        Assert.Equal(TimeSpan.FromSeconds(12), live.Time);
        Assert.Empty(StoredBookmarks(Path.GetFileName(active)));

        Assert.True(_host.StopRecording());
    }

    [Fact]
    public void AddBookmark_UsesTheSuppliedIdSoItCanBeDeletedAgain()
    {
        var id = Guid.NewGuid();

        _host.AddBookmark(new AddBookmarkParameters
        {
            FilePath = "sessions/old.mp4",
            Id = id.ToString(),
            Time = 5,
            Type = "manual",
        });

        Assert.Equal(id, Assert.Single(StoredBookmarks("old.mp4")).Id);

        _host.DeleteBookmark(new DeleteBookmarkParameters
        {
            FilePath = "sessions/old.mp4",
            Id = id.ToString(),
        });

        Assert.Empty(StoredBookmarks("old.mp4"));
    }

    [Fact]
    public void DeleteBookmark_ForTheActiveRecording_RemovesItFromTheLiveSession()
    {
        Assert.True(_host.StartRecording(gameId: null));
        var active = ActiveSessionRelativePath();
        var id = Guid.NewGuid();

        _host.AddBookmark(new AddBookmarkParameters
        {
            FilePath = active,
            Id = id.ToString(),
            Time = 12,
            Type = "manual",
        });
        Assert.Single(_tracker.Active!.Bookmarks);

        _host.DeleteBookmark(new DeleteBookmarkParameters
        {
            FilePath = active,
            Id = id.ToString(),
        });

        Assert.Empty(_tracker.Active!.Bookmarks);
        Assert.True(_host.StopRecording());
    }
}
