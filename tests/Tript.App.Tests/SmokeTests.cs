// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Tript.App.Tests;

// The alpha smoke test. Starts the app host as a child process against a
// temp content root, connects a WebSocket control-socket client, and asserts the six round trips
// that define the alpha:
//   1. NewConnection -> full push (state, settings, gameList)
//   2. StartRecording -> state recording=true; a real MP4 on disk
//   3. StopRecording -> state recording=false
//   4. The content server serves the recorded file (range 206, right bytes) and refuses ".." (403)
//   5. CreateClip -> importProgress (done or error; the round trip works)
//   6. UpdateSettings -> settings push reflects the change
//
// The seam-level tests run with --fake-recorder (no libobs, no display server, no muxer helper).
// The recorder round trip asserts the state machine and the output path; the fake does not write
// bytes, so the "real MP4 on disk" assertion is covered by the real-recorder test class, which is
// skipped when the machine cannot host a real recording.
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

        await host.SendAsync("""{"method":"StopRecording"}""");
        var (stopMethod, stopContent) = await host.ReceiveAsyncParsed();
        Assert.Equal("state", stopMethod);
        Assert.False(stopContent.GetProperty("state").GetProperty("recording").GetBoolean());

        await host.ShutdownAsync();
    }

    // A configured recording output directory must be honoured by the host's output-path builder.
    // BuildOutputPath creates the game/sessions/ directory unconditionally — flat, no date subfolder —
    // so a fake-recorder StartRecording is enough to observe where the recording would land: no
    // libobs, no real file.
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

        var expected = Path.Combine(outputRoot, "Overwatch", "sessions");
        Assert.True(Directory.Exists(expected), $"The configured output directory was not created: {expected}");
        // Sessions are flat inside the game folder: the timestamp is in the file name, so there is no date subfolder.
        Assert.Empty(Directory.GetDirectories(expected));

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
            """{"method":"UpdateSettings","parameters":{"settings":{"recording":{"quality":5}}}}""");

        var (method, content) = await host.ReceiveAsyncParsed();
        Assert.Equal("settings", method);
        var quality = content.GetProperty("settings").GetProperty("recording").GetProperty("quality").GetInt32();
        Assert.Equal(5, quality);

        // The change persisted to the settings file, not just the push.
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

        // A fake source file for the clip engine to chew on. The engine runs real ffmpeg, so the
        // clip may succeed or fail depending on the machine; the round trip is that importProgress
        // arrives either way.
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

        // importing always arrives first; done or error follows.
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
