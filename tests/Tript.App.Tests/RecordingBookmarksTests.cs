// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.App.Content;
using Tript.Core;
using Xunit;

namespace Tript.App.Tests;

public sealed class RecordingBookmarksTests : IDisposable
{
    private readonly string _root;
    private readonly RecordingMetadataStore _metadata;
    private readonly List<string> _errors = [];
    private readonly RecordingBookmarks _bookmarks;

    public RecordingBookmarksTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tript-recording-bookmarks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _metadata = new RecordingMetadataStore(ContentLayout.MetadataRoot(_root));
        _bookmarks = new RecordingBookmarks(_metadata, path => ContentLayout.ToWirePath(_root, path), _errors.Add);
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

    [Fact]
    public void Create_KeepsAValidIdAndType_AndDefaultsTheRest()
    {
        var id = Guid.NewGuid();
        var kept = RecordingBookmarks.Create(new AddBookmarkParameters { Id = id.ToString(), Type = "kill", Time = 2.5 });
        var defaulted = RecordingBookmarks.Create(new AddBookmarkParameters { Id = "nope", Type = "nonsense", Time = 1 });

        Assert.Equal(id, kept.Id);
        Assert.Equal(BookmarkType.Kill, kept.Type);
        Assert.Equal(TimeSpan.FromSeconds(2.5), kept.Time);
        Assert.NotEqual(Guid.Empty, defaulted.Id);
        Assert.Equal(BookmarkType.Manual, defaulted.Type);
    }

    [Fact]
    public void TryAdd_ToARecordingWithoutMetadata_CreatesTheRecord()
    {
        var target = Path.Combine(_root, "sessions", "a.mp4");
        var bookmark = new Bookmark { Id = Guid.NewGuid(), Time = TimeSpan.FromSeconds(3) };

        Assert.True(_bookmarks.TryAdd(target, bookmark));

        var record = _metadata.Load("a.mp4");
        Assert.NotNull(record);
        Assert.Equal("sessions/a.mp4", record.VideoPath);
        Assert.Equal(bookmark.Id, Assert.Single(record.Bookmarks).Id);
        Assert.Empty(_errors);
    }

    [Fact]
    public void TryAdd_ToAnUnreadableRecord_RefusesAndLeavesItAlone()
    {
        var path = _metadata.PathFor("a.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");

        Assert.False(_bookmarks.TryAdd(Path.Combine(_root, "sessions", "a.mp4"), new Bookmark()));

        Assert.Equal("{ not json", File.ReadAllText(path));
        Assert.Single(_errors);
    }

    [Fact]
    public void TryRemove_RemovesOnlyTheMatchingBookmark()
    {
        var target = Path.Combine(_root, "sessions", "a.mp4");
        var keep = new Bookmark { Id = Guid.NewGuid() };
        var drop = new Bookmark { Id = Guid.NewGuid() };
        _bookmarks.TryAdd(target, keep);
        _bookmarks.TryAdd(target, drop);

        Assert.True(_bookmarks.TryRemove("a.mp4", drop.Id));
        Assert.False(_bookmarks.TryRemove("a.mp4", drop.Id));
        Assert.False(_bookmarks.TryRemove("missing.mp4", keep.Id));

        Assert.Equal(keep.Id, Assert.Single(_metadata.Load("a.mp4")!.Bookmarks).Id);
        Assert.Empty(_errors);
    }
}
