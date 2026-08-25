// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.App.Content;
using Tript.Core;
using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

// The trash as it appears on the control socket. The field names, the casing and the units are a
// compatibility surface the frontend narrows on, so they are asserted through a real host rather
// than against the in-process model.
[Collection(AppHostCollection.Name)]
public sealed class TrashWireTests
{
    private readonly string _contentRoot;
    private readonly string _settingsPath;

    public TrashWireTests(AppHostCollectionFixture fixture)
    {
        _contentRoot = fixture.NewContentRoot(nameof(TrashWireTests));
        _settingsPath = fixture.NewSettingsPath(nameof(TrashWireTests));
    }

    [Fact]
    public async Task Delete_PushesContentThenTrash_AndTheEntryRoundTripsThroughRestore()
    {
        var sessions = Path.Combine(_contentRoot, "sessions");
        Directory.CreateDirectory(sessions);
        await File.WriteAllTextAsync(Path.Combine(sessions, "session-1.mp4"), "session");

        var store = new RecordingMetadataStore(Path.Combine(_contentRoot, "metadata"));
        store.Save(new RecordingMetadata
        {
            VideoPath = "sessions/session-1.mp4",
            Game = "Overwatch",
            Title = "The clutch",
            Bookmarks = { new Bookmark { Type = BookmarkType.Kill, Time = TimeSpan.FromSeconds(1) } },
        });

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync(
            """{"method":"DeleteContent","parameters":{"fileName":"sessions/session-1.mp4","contentType":"recording"}}""");

        var (contentMethod, content) = await host.ReceiveAsyncParsed();
        Assert.Equal("content", contentMethod);
        Assert.Empty(content.GetProperty("content").EnumerateArray());

        var (trashMethod, trash) = await host.ReceiveAsyncParsed();
        Assert.Equal("trash", trashMethod);
        Assert.Equal(24, trash.GetProperty("retentionHours").GetInt32());

        var entry = Assert.Single(trash.GetProperty("entries").EnumerateArray().ToList());
        var id = entry.GetProperty("id").GetString()!;
        Assert.NotEmpty(id);
        Assert.Equal("recording", entry.GetProperty("contentType").GetString());
        Assert.Equal("session-1.mp4", entry.GetProperty("fileName").GetString());
        Assert.Equal("The clutch", entry.GetProperty("title").GetString());
        Assert.Equal("Overwatch", entry.GetProperty("game").GetString());
        Assert.True(entry.GetProperty("fileSizeBytes").GetInt64() > 0);

        // Epoch seconds, not milliseconds: the window between them is the retention exactly.
        var deletedAt = entry.GetProperty("deletedAt").GetInt64();
        var purgeAt = entry.GetProperty("purgeAt").GetInt64();
        Assert.InRange(deletedAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 120,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 120);
        Assert.Equal(deletedAt + 24 * 3600, purgeAt);

        // ListTrash answers with the same push.
        await host.SendAsync("""{"method":"ListTrash"}""");
        var (listed, listedTrash) = await host.ReceiveAsyncParsed();
        Assert.Equal("trash", listed);
        Assert.Equal(id, listedTrash.GetProperty("entries")[0].GetProperty("id").GetString());

        await host.SendAsync(
            "{\"method\":\"RestoreTrash\",\"parameters\":{\"entryIds\":[\"" + id + "\"]}}");
        var (restoredContent, restored) = await host.ReceiveAsyncParsed();
        Assert.Equal("content", restoredContent);
        Assert.Single(restored.GetProperty("content").EnumerateArray());
        var (restoredTrash, emptied) = await host.ReceiveAsyncParsed();
        Assert.Equal("trash", restoredTrash);
        Assert.Empty(emptied.GetProperty("entries").EnumerateArray());

        Assert.True(File.Exists(Path.Combine(sessions, "session-1.mp4")));

        await host.ShutdownAsync();
    }

    // PurgeTrash with no parameters at all empties the whole bin — the command has to survive the
    // absent-parameters case rather than being dropped as malformed.
    [Fact]
    public async Task PurgeTrash_WithoutParameters_EmptiesTheBin()
    {
        var sessions = Path.Combine(_contentRoot, "sessions");
        Directory.CreateDirectory(sessions);
        await File.WriteAllTextAsync(Path.Combine(sessions, "session-1.mp4"), "session");

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync(
            """{"method":"DeleteContent","parameters":{"fileName":"sessions/session-1.mp4","contentType":"recording"}}""");
        await DrainPushes(host, 2);

        await host.SendAsync("""{"method":"PurgeTrash"}""");
        await DrainPushes(host, 1);
        var (method, trash) = await host.ReceiveAsyncParsed();
        Assert.Equal("trash", method);
        Assert.Empty(trash.GetProperty("entries").EnumerateArray());

        // The bin is gone from disk too, not just from the listing.
        Assert.Empty(Directory.GetDirectories(Path.Combine(_contentRoot, ".trash")));

        await host.ShutdownAsync();
    }

    private static async Task DrainPushes(AppHostDriver host, int count)
    {
        for (var index = 0; index < count; index++)
            await host.ReceiveAsyncParsed();
    }
}
