// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Tript.Obs;
using Xunit;
using Xunit.Sdk;

namespace Tript.App.Tests;

// The real-recorder smoke test: the app host starts libobs and records a real MP4 through the
// ffmpeg_muxer plugin. This exercises the full recording path — ObsRuntime, the recorder state
// machine, and the muxer helper — which the --fake-recorder tests cannot.
[Collection(AppHostCollection.Name)]
public sealed class RealRecorderSmokeTests : IDisposable
{
    private readonly AppHostCollectionFixture _fixture;
    private readonly string _contentRoot;
    private readonly string _settingsPath;

    public RealRecorderSmokeTests(AppHostCollectionFixture fixture)
    {
        _fixture = fixture;
        _contentRoot = fixture.NewContentRoot(nameof(RealRecorderSmokeTests));
        _settingsPath = fixture.NewSettingsPath(nameof(RealRecorderSmokeTests));
    }

    public void Dispose()
    {
    }

    [Fact]
    public async Task Real_recording_writes_an_mp4_and_the_content_server_serves_it()
    {
        if (!CanRunRealRecording(out var reason))
            throw SkipException.ForSkip(reason);

        // The app host symlinks the system obs-ffmpeg-mux beside its own binary at startup
        // (MuxerHelper.EnsureNextToApp), so no copy is needed here.

        var host = AppHostDriver.StartReal(_contentRoot, _settingsPath);
        await using var _ = host;

        await host.ConnectWebSocketAsync();
        await DrainUntil(host, "gameList");

        await host.SendAsync("""{"method":"StartRecording"}""");
        var (startMethod, startContent) = await DrainUntilState(host);
        Assert.True(startContent.GetProperty("state").GetProperty("recording").GetBoolean());

        // Let the recorder produce real frames; the alpha records a Session through the muxer at
        // whatever the settings say (a fresh settings file starts at this machine's primary display
        // resolution — Program.ApplyFirstRunDefaults), so a few seconds is plenty for a real file.
        await Task.Delay(TimeSpan.FromSeconds(4));

        await host.SendAsync("""{"method":"StopRecording"}""");
        var (stopMethod, stopContent) = await DrainUntilState(host);
        Assert.False(stopContent.GetProperty("state").GetProperty("recording").GetBoolean());

        // The real recording produced an MP4 under the content root.
        var files = Directory.GetFiles(_contentRoot, "*.mp4", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        var recorded = files.Single();
        Assert.True(new FileInfo(recorded).Length > 0, "The recorded file is empty.");

        // The content server serves it: a range request returns 206 with the first byte.
        var relative = Path.GetRelativePath(_contentRoot, recorded).Replace(Path.DirectorySeparatorChar, '/');
        var range = await RawRangeAsync(relative, "bytes=0-0");
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.StartsWith("bytes 0-0/", range.ContentRange);

        await host.ShutdownAsync();
    }

    private static bool CanRunRealRecording(out string reason)
    {
        reason = string.Empty;

        // The host needs a reachable X display.
        var display = Environment.GetEnvironmentVariable("DISPLAY");
        if (string.IsNullOrWhiteSpace(display))
        {
            reason = "No DISPLAY set; the real recorder needs an X server.";
            return false;
        }

        // Asks the app's own resolver rather than restating where the helper lives: a second copy of
        // that list is how this skipped on a machine that had the helper all along.
        if (MuxerHelper.ResolveSystemHelper(ObsRuntimeLocator.Discover().ModuleBinaryDir) is null)
        {
            reason = "No obs-ffmpeg-mux helper on this machine; the ffmpeg_muxer plugin cannot record.";
            return false;
        }

        return true;
    }

    private static async Task DrainPushes(AppHostDriver host, int count)
    {
        for (var i = 0; i < count; i++)
            await host.ReceiveAsyncParsed();
    }

    private static async Task DrainUntil(AppHostDriver host, string method)
    {
        for (var i = 0; i < 10; i++)
        {
            var (m, _) = await host.ReceiveAsyncParsed();
            if (m == method)
                return;
        }

        throw new InvalidOperationException($"The host never pushed '{method}'.");
    }

    // Reads pushes until a "state" push arrives; the real host can interleave gameList pushes
    // (the detector and the catalogue) around StartRecording, so the state push is not necessarily
    // the first frame after the command.
    private static async Task<(string Method, JsonElement Content)> DrainUntilState(AppHostDriver host)
    {
        for (var i = 0; i < 10; i++)
        {
            var (m, content) = await host.ReceiveAsyncParsed();
            if (m == "state")
                return (m, content);
        }

        throw new InvalidOperationException("The host never pushed 'state'.");
    }

    private static async Task<(HttpStatusCode StatusCode, string ContentRange)> RawRangeAsync(string path, string range)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("localhost", 2222);
        await using var stream = client.GetStream();
        var request = $"GET /api/content/{path} HTTP/1.1\r\nHost: localhost:2222\r\nRange: {range}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));

        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
            ms.Write(buffer, 0, read);

        var responseText = Encoding.ASCII.GetString(ms.ToArray());
        var statusLine = responseText.Split('\r', '\n').First();
        var code = int.Parse(statusLine.Split(' ')[1]);
        var contentRange = responseText
            .Split("\r\n")
            .FirstOrDefault(line => line.StartsWith("Content-Range:", StringComparison.OrdinalIgnoreCase))
            ?.Substring("Content-Range:".Length)
            .Trim() ?? string.Empty;

        return ((HttpStatusCode)code, contentRange);
    }
}
