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
}
