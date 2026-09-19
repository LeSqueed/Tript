// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Xunit;

namespace Tript.App.Tests;

[Collection(AppHostCollection.Name)]
public sealed class StorageWireTests
{
    private readonly string _contentRoot;
    private readonly string _settingsPath;

    public StorageWireTests(AppHostCollectionFixture fixture)
    {
        _contentRoot = fixture.NewContentRoot(nameof(StorageWireTests));
        _settingsPath = fixture.NewSettingsPath(nameof(StorageWireTests));
    }

    private async Task<JsonElement> WaitForAsync(AppHostDriver host, string method)
    {
        for (var frame = 0; frame < 20; frame++)
        {
            var (received, content) = await host.ReceiveAnyParsed();
            if (received == method)
                return content;
        }

        throw new Xunit.Sdk.XunitException($"The host never sent a '{method}' frame.");
    }

    [Fact]
    public async Task GetStorageReport_AnswersWithTheSplitOfTheRecordingFolder()
    {
        var sessions = Path.Combine(_contentRoot, "Overwatch", "sessions");
        var highlights = Path.Combine(_contentRoot, "Overwatch", "highlights");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(highlights);
        await File.WriteAllBytesAsync(Path.Combine(sessions, "session-1.mp4"), new byte[4096]);
        await File.WriteAllBytesAsync(Path.Combine(highlights, "session-1-highlight-1.mp4"), new byte[1024]);

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();

        await host.SendAsync("""{"method":"GetStorageReport"}""");
        var report = await WaitForAsync(host, "storageReport");

        Assert.Equal(4096, report.GetProperty("sessionBytes").GetInt64());
        Assert.Equal(1024, report.GetProperty("highlightBytes").GetInt64());
        Assert.Equal(1, report.GetProperty("sessionCount").GetInt32());
        Assert.Equal(1, report.GetProperty("highlightCount").GetInt32());
        Assert.NotEmpty(report.GetProperty("games").EnumerateArray().ToList());
        Assert.True(report.GetProperty("libraryBytes").GetInt64() >= 4096 + 1024);
    }

    [Fact]
    public async Task GetStorageStatus_AnswersWithTheFloorAndTheWarnLine()
    {
        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();

        await host.SendAsync("""{"method":"GetStorageStatus"}""");
        var status = await WaitForAsync(host, "storageStatus");

        var floor = status.GetProperty("minimumFreeBytes").GetInt64();
        Assert.Equal(20L * 1024 * 1024 * 1024, floor);
        Assert.Equal(floor * 3, status.GetProperty("warnFreeBytes").GetInt64());
        Assert.False(status.GetProperty("recordingBlocked").GetBoolean());
    }

    [Fact]
    public async Task AContentChange_AlsoRefreshesTheStorageReport()
    {
        var sessions = Path.Combine(_contentRoot, "sessions");
        Directory.CreateDirectory(sessions);
        await File.WriteAllBytesAsync(Path.Combine(sessions, "session-1.mp4"), new byte[2048]);

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();

        await host.SendAsync("""{"method":"ListContent"}""");
        var report = await WaitForAsync(host, "storageReport");

        Assert.Equal(2048, report.GetProperty("sessionBytes").GetInt64());
    }
}
