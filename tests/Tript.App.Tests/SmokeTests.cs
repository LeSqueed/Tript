// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Tript.App.Tests;

[Collection(AppHostCollection.Name)]
public sealed class SmokeTests : IDisposable
{
    private readonly AppHostCollectionFixture _fixture;
    private readonly string _contentRoot;
    private readonly string _settingsPath;
    private readonly string _testName;

    public SmokeTests(AppHostCollectionFixture fixture)
    {
        _fixture = fixture;
        _testName = GetType().Name;
        _contentRoot = fixture.NewContentRoot(_testName);
        _settingsPath = fixture.NewSettingsPath(_testName);
    }

    public void Dispose()
    {
    }

    [Fact]
    public async Task NewConnection_pushes_state_settings_and_gameList()
    {
        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();

        var (method1, content1) = await host.ReceiveAsyncParsed();
        Assert.Equal("state", method1);
        Assert.False(content1.GetProperty("state").GetProperty("recording").GetBoolean());

        var (method2, _) = await host.ReceiveAsyncParsed();
        Assert.Equal("settings", method2);

        var (method3, _) = await host.ReceiveAsyncParsed();
        Assert.Equal("gameList", method3);

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task StartRecording_and_StopRecording_round_trip()
    {
        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"StartRecording"}""");
        var (startMethod, startContent) = await host.ReceiveAsyncParsed();
        Assert.Equal("state", startMethod);
        Assert.True(startContent.GetProperty("state").GetProperty("recording").GetBoolean());
        var (startedContentMethod, _) = await host.ReceiveAsyncParsed();
        Assert.Equal("content", startedContentMethod);

        await host.SendAsync("""{"method":"StopRecording"}""");
        var (stopMethod, stopContent) = await host.ReceiveAsyncParsed();
        Assert.Equal("state", stopMethod);
        Assert.False(stopContent.GetProperty("state").GetProperty("recording").GetBoolean());

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task StartRecording_withConfiguredOutputDirectory_createsIt()
    {
        var outputRoot = Path.Combine(_contentRoot, "custom-recordings");
        var seeded = new Tript.Settings.Settings { Recording = { OutputDirectory = outputRoot } };
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath, Tript.Settings.SettingsSerialization.Serialize(seeded));

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"StartRecording"}""");
        var (startMethod, startContent) = await host.ReceiveAsyncParsed();
        Assert.Equal("state", startMethod);
        Assert.True(startContent.GetProperty("state").GetProperty("recording").GetBoolean());

        var expected = Path.Combine(outputRoot, "sessions");
        Assert.True(Directory.Exists(expected), $"The configured output directory was not created: {expected}");

        Assert.Empty(Directory.GetDirectories(expected));

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task StartRecording_withStaleGameId_clearsGameAttributionForDesktopCapture()
    {
        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"StartRecording","parameters":{"gameId":"Overwatch"}}""");
        var (method, content) = await host.ReceiveAsyncParsed();

        Assert.Equal("state", method);
        var state = content.GetProperty("state");
        Assert.True(state.GetProperty("recording").GetBoolean());
        Assert.False(state.TryGetProperty("game", out var _ignoredGame));
        Assert.True(Directory.Exists(Path.Combine(_contentRoot, "sessions")));
        Assert.False(Directory.Exists(Path.Combine(_contentRoot, "Overwatch", "sessions")));

        await host.SendAsync("""{"method":"StopRecording"}""");
        await host.ReceiveAsyncParsed();
        await host.ShutdownAsync();
    }

    [Fact]
    public async Task StartRecording_inGameModeWithoutDetectedProcess_reportsSpecificError()
    {
        var seeded = new Tript.Settings.Settings
        {
            Capture = { Method = Tript.Settings.DisplayCaptureMethod.Game },
        };
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath, Tript.Settings.SettingsSerialization.Serialize(seeded));
        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"StartRecording","parameters":{"gameId":"Overwatch"}}""");
        var (method, content) = await host.ReceiveAsyncParsed();

        Assert.Equal("error", method);
        Assert.Equal(
            "Recording did not start because no game is detected. Set the capture method to Auto or Display to record the desktop.",
            content.GetProperty("message").GetString());

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task OneOffDisplayOverride_doesNotMutateSavedDisplay()
    {
        var seeded = new Tript.Settings.Settings
        {
            Capture =
            {
                Method = Tript.Settings.DisplayCaptureMethod.Display,
                Display = "saved-display",
                DisplayLabel = "Saved display",
            },
        };
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath, Tript.Settings.SettingsSerialization.Serialize(seeded));
        var settingsTrace = Path.Combine(_contentRoot, "fake-recorder-settings.jsonl");
        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath,
            fakeRecorderSettingsTrace: settingsTrace);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""
            {"method":"StartRecording","parameters":{"applyDisplay":true,"displayId":"one-off-display"}}
            """);
        var (method, content) = await host.ReceiveAsyncParsed();
        Assert.Equal("state", method);
        Assert.True(content.GetProperty("state").GetProperty("recording").GetBoolean());
        await host.ReceiveAsyncParsed();

        var onDisk = JsonDocument.Parse(File.ReadAllText(_settingsPath)).RootElement.GetProperty("capture");
        Assert.Equal("saved-display", onDisk.GetProperty("display").GetString());
        Assert.Equal("Saved display", onDisk.GetProperty("displayLabel").GetString());

        await host.SendAsync("""{"method":"StopRecording"}""");
        await host.ReceiveAsyncParsed();

        await host.SendAsync("""{"method":"StartRecording"}""");
        var (secondMethod, secondContent) = await host.ReceiveAsyncParsed();
        Assert.Equal("state", secondMethod);
        Assert.True(secondContent.GetProperty("state").GetProperty("recording").GetBoolean());
        await host.ReceiveAsyncParsed();

        var effectiveDisplays = File.ReadAllLines(settingsTrace)
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("Display").GetString())
            .ToList();
        Assert.Equal(["one-off-display", "saved-display"], effectiveDisplays);

        await host.SendAsync("""{"method":"StopRecording"}""");
        await host.ReceiveAsyncParsed();
        await host.ShutdownAsync();
    }

    [Fact]
    public async Task UpdateSettings_pushes_the_change()
    {
        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync(
            """{"method":"UpdateSettings","parameters":{"requestId":"settings-1","settings":{"recording":{"quality":5}}}}""");

        var (method, content) = await host.ReceiveAsyncParsed();
        Assert.Equal("settings", method);
        var quality = content.GetProperty("settings").GetProperty("recording").GetProperty("quality").GetInt32();
        Assert.Equal(5, quality);

        var (resultMethod, result) = await host.ReceiveAsyncParsed();
        Assert.Equal("settingsUpdateResult", resultMethod);
        Assert.Equal("settings-1", result.GetProperty("requestId").GetString());
        Assert.True(result.GetProperty("success").GetBoolean());

        var onDisk = JsonDocument.Parse(File.ReadAllText(_settingsPath));
        Assert.Equal(5, onDisk.RootElement.GetProperty("recording").GetProperty("quality").GetInt32());

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task CreateClip_round_trips_importProgress()
    {
        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        var source = Path.Combine(_contentRoot, "sessions", "source.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "not a real mp4 but the path is what matters");

        var request = """
            {"method":"CreateClip","parameters":{
              "id":"clip-test-1",
              "filePath":"sessions/source.mp4",
              "outputMode":"combine",
              "startTime":0,"endTime":1,
              "segments":[]
            }}
            """;
        await host.SendAsync(request);

        var (m1, _) = await host.ReceiveAsyncParsed();
        Assert.Equal("importProgress", m1);

        var (m2, content2) = await host.ReceiveAsyncParsed();
        Assert.Equal("importProgress", m2);
        var status = content2.GetProperty("status").GetString();
        Assert.True(status == "done" || status == "error",
            $"importProgress should be done or error, was '{status}'");

        await host.ShutdownAsync();
    }

    private static async Task DrainPushes(AppHostDriver host, int count)
    {
        for (var i = 0; i < count; i++)
            await host.ReceiveAsyncParsed();
    }
}
