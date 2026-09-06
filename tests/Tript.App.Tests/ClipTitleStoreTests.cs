// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.App.Content;
using Xunit;

namespace Tript.App.Tests;

public sealed class ClipTitleStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tript-app-tests",
        nameof(ClipTitleStoreTests), Guid.NewGuid().ToString("N"));

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
    public void EnumerateRecords_ReturnsValidRecordsAndIgnoresUnusableFiles()
    {
        Directory.CreateDirectory(_root);
        var store = new ClipTitleStore(_root);
        Assert.True(store.SaveAutomatic("highlight-a.mp4", "sessions/session-a.mp4", 10, 20));
        File.WriteAllText(Path.Combine(_root, "broken.mp4.title.json"), "{ not json");
        File.WriteAllText(Path.Combine(_root, "not-a-record.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_root, "nested"));
        File.WriteAllText(Path.Combine(_root, "nested", "nested.mp4.title.json"), "{}");

        var records = store.EnumerateRecords();

        var entry = Assert.Single(records);
        Assert.Equal("highlight-a.mp4", entry.ClipFileName);
        Assert.True(entry.Record.IsAutomatic);
        Assert.Equal("sessions/session-a.mp4", entry.Record.SourceSessionPath);

        Assert.True(store.Save("later.mp4", "Later"));
        Assert.Single(records);
    }

    [Fact]
    public void EnumerateRecords_ReturnsEmptyWhenMetadataRootIsMissing()
    {
        var store = new ClipTitleStore(_root);

        Assert.Empty(store.EnumerateRecords());
    }

    [Fact]
    public void SaveAutomatic_PersistsHighlightsOnlySessionOwnership()
    {
        var store = new ClipTitleStore(_root);

        Assert.True(store.SaveAutomatic("highlight-a.mp4", "sessions/session-a.mp4", 10, 20,
            sourceSessionHighlightsOnly: true));

        var record = Assert.Single(store.EnumerateRecords()).Record;
        Assert.True(record.SourceSessionHighlightsOnly);
        Assert.Equal("sessions/session-a.mp4", record.SourceSessionPath);
    }

    [Fact]
    public void SaveAutomaticAssignment_ChangesOwnershipWithoutDroppingClipMetadata()
    {
        var store = new ClipTitleStore(_root);
        Assert.True(store.Save("highlight-a.mp4", "Clutch"));
        Assert.True(store.SaveFavorite("highlight-a.mp4", true));
        Assert.True(store.SaveDuration("highlight-a.mp4", 12.5));
        Assert.True(store.SaveHdrStatus("highlight-a.mp4", true));
        Assert.True(store.SaveAutomatic("highlight-a.mp4", "Old Game/sessions/session-a.mp4", 10, 20));
        Assert.True(store.SaveGame("highlight-a.mp4", "Old Game", "old-game"));

        Assert.True(store.SaveAutomaticAssignment("highlight-a.mp4",
            "New Game/sessions/session-a.mp4", "New Game", "new-game"));

        var record = store.LoadRecord("highlight-a.mp4");
        Assert.NotNull(record);
        Assert.Equal("Clutch", record.Title);
        Assert.True(record.Favorite);
        Assert.Equal(12.5, record.DurationSeconds);
        Assert.True(record.IsHdr);
        Assert.True(record.IsAutomatic);
        Assert.Equal(10, record.ClipStartTime);
        Assert.Equal(20, record.ClipEndTime);
        Assert.Equal("New Game/sessions/session-a.mp4", record.SourceSessionPath);
        Assert.Equal("New Game", record.Game);
        Assert.Equal("new-game", record.GameId);
    }
}
