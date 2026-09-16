// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Net.WebSockets;
using System.Text;
using Tript.App.Ipc;
using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

[Collection(AppHostCollection.Name)]
public sealed class ControlSocketBoundsTests
{
    private readonly string _contentRoot;
    private readonly string _settingsPath;

    public ControlSocketBoundsTests(AppHostCollectionFixture fixture)
    {
        _contentRoot = fixture.NewContentRoot(nameof(ControlSocketBoundsTests));
        _settingsPath = fixture.NewSettingsPath(nameof(ControlSocketBoundsTests));
    }

    [Fact]
    public async Task AMessageOverTheCap_ClosesTheConnection()
    {
        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(host.WithToken($"ws://localhost:{host.ControlPort}/")), Cancel);

        var padding = new string('a', IpcServer.ClientConnection.MaxInboundMessageBytes * 2);
        await socket.SendAsync(
            Encoding.UTF8.GetBytes("{\"method\":\"NewConnection\",\"parameters\":{\"pad\":\"" + padding + "\"}}"),
            WebSocketMessageType.Text, endOfMessage: true, Cancel);

        var closeStatus = await WaitForCloseAsync(socket);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, closeStatus);

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task AMessageUnderTheCap_IsProcessedAndTheConnectionSurvives()
    {
        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        var padding = new string('a', IpcServer.ClientConnection.MaxInboundMessageBytes / 2);
        await host.SendAsync("{\"method\":\"NoSuchCommand\",\"parameters\":{\"pad\":\"" + padding + "\"}}");

        await host.SendAsync("""{"method":"ListTrash"}""");
        var (method, _) = await host.ReceiveAsyncParsed();
        Assert.Equal("trash", method);

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task StopRecording_WithNothingRecording_PushesAnError()
    {
        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"StopRecording"}""");

        var (method, content) = await host.ReceiveAsyncParsed();
        Assert.Equal("error", method);
        Assert.False(string.IsNullOrWhiteSpace(content.GetProperty("message").GetString()));

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task StartRecording_WhileAlreadyRecording_PushesAnError()
    {
        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"StartRecording"}""");
        var (started, startedContent) = await host.ReceiveAsyncParsed();
        Assert.Equal("state", started);
        Assert.True(startedContent.GetProperty("state").GetProperty("recording").GetBoolean());
        await host.ReceiveAsyncParsed();

        await host.SendAsync("""{"method":"StartRecording"}""");
        var (method, content) = await host.ReceiveAsyncParsed();
        Assert.Equal("error", method);
        Assert.False(string.IsNullOrWhiteSpace(content.GetProperty("message").GetString()));

        await host.SendAsync("""{"method":"StopRecording"}""");
        var (stopped, _) = await host.ReceiveAsyncParsed();
        Assert.Equal("state", stopped);

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task ReplayBufferOnly_StartStateReportsItsActiveMode()
    {
        var settings = new SettingsStore(new SettingsFileProvider(_settingsPath));
        settings.Load().Recording.Mode = RecordingMode.ReplayBufferOnly;
        settings.Save();
        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"StartRecording"}""");
        var (method, content) = await host.ReceiveAsyncParsed();

        Assert.Equal("state", method);
        var state = content.GetProperty("state");
        Assert.True(state.GetProperty("recording").GetBoolean());
        Assert.Equal("ReplayBufferOnly", state.GetProperty("activeRecordingMode").GetString());

        await host.SendAsync("""{"method":"StopRecording"}""");
        await host.ReceiveAsyncParsed();
        await host.ShutdownAsync();
    }

    [Fact]
    public async Task ConvertToSdr_WhileRecording_IsRejectedBeforeMediaWorkStarts()
    {
        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"StartRecording","parameters":{"gameId":"Overwatch"}}""");
        var (started, _) = await host.ReceiveAsyncParsed();
        Assert.Equal("state", started);
        await host.ReceiveAsyncParsed();

        await host.SendAsync("""{"method":"ConvertToSdr","parameters":{"id":"sdr-during-recording","contentType":"clip","filePath":"clips/missing.mp4"}}""");
        var (method, content) = await host.ReceiveAsyncParsed();
        Assert.Equal("importProgress", method);
        Assert.Equal("sdr-during-recording", content.GetProperty("id").GetString());
        Assert.Equal("error", content.GetProperty("status").GetString());
        Assert.Contains("while recording", content.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);

        await host.SendAsync("""{"method":"StopRecording"}""");
        await host.ReceiveAsyncParsed();
        await host.ShutdownAsync();
    }

    [Fact]
    public void AClientThatIsNotRead_DropsFramesRatherThanQueueingThemForever()
    {
        var connection = new IpcServer.ClientConnection(new NeverReadSocket(), new IpcServer(null!, new SessionToken()));

        for (var index = 0; index < 4000; index++)
            connection.Send("{\"method\":\"state\",\"content\":{}}");

        Assert.True(connection.DroppedFrames > 0,
            "an outbound queue nothing is draining must drop frames, not grow without limit");
    }

    private sealed class NeverReadSocket : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override WebSocketState State => WebSocketState.Open;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken t) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken t) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken t) =>
            new TaskCompletionSource<WebSocketReceiveResult>().Task;
        public override Task SendAsync(ArraySegment<byte> b, WebSocketMessageType m, bool e, CancellationToken t) =>
            new TaskCompletionSource().Task;
    }

    private static CancellationToken Cancel => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private static async Task<WebSocketCloseStatus?> WaitForCloseAsync(ClientWebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), Cancel);
            }
            catch (WebSocketException)
            {
                return socket.CloseStatus;
            }

            if (result.MessageType == WebSocketMessageType.Close)
                return result.CloseStatus;
        }
    }

    private static async Task DrainPushes(AppHostDriver host, int count)
    {
        for (var index = 0; index < count; index++)
            await host.ReceiveAsyncParsed();
    }
}
