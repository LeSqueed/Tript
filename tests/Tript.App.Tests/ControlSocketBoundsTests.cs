// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Net.WebSockets;
using System.Text;
using Tript.App.Ipc;
using Xunit;

namespace Tript.App.Tests;

// The control socket's limits, and the refusals it has to report.
//
// The receive loop used to accumulate WebSocket continuation frames into a MemoryStream with no
// ceiling and then double the peak with ToArray(), so one client message of arbitrary length was an
// unbounded allocation in the app's own process — reachable by anything that gets past the Origin
// check, including the app's own UI with a bug in it.
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
        await socket.ConnectAsync(new Uri(host.WithToken($"ws://localhost:{TestPorts.ControlSocket}/")), Cancel);

        // Well past the cap, and valid JSON throughout, so nothing but the size can be refusing it.
        var padding = new string('a', IpcServer.ClientConnection.MaxInboundMessageBytes * 2);
        await socket.SendAsync(
            Encoding.UTF8.GetBytes("{\"method\":\"NewConnection\",\"parameters\":{\"pad\":\"" + padding + "\"}}"),
            WebSocketMessageType.Text, endOfMessage: true, Cancel);

        var closeStatus = await WaitForCloseAsync(socket);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, closeStatus);

        await host.ShutdownAsync();
    }

    // The other side of the cap: an ordinary command is nowhere near it, and a large-but-allowed
    // message must not cost the connection either.
    [Fact]
    public async Task AMessageUnderTheCap_IsProcessedAndTheConnectionSurvives()
    {
        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        // An unknown method is dropped silently, so this proves only that the frame was read whole.
        var padding = new string('a', IpcServer.ClientConnection.MaxInboundMessageBytes / 2);
        await host.SendAsync("{\"method\":\"NoSuchCommand\",\"parameters\":{\"pad\":\"" + padding + "\"}}");

        // The connection still answers, which it would not if the big frame had torn it down.
        await host.SendAsync("""{"method":"ListTrash"}""");
        var (method, _) = await host.ReceiveAsyncParsed();
        Assert.Equal("trash", method);

        await host.ShutdownAsync();
    }

    // ---- the refusals the dispatch entry points report ----

    // StartRecording and StopRecording answer bool, and every refusal returns before the state push.
    // A caller that drops the bool leaves the user pressing a button and seeing nothing change at
    // all — no state frame, no error, nothing.
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
    public async Task ConvertToSdr_WhileRecording_IsRejectedBeforeMediaWorkStarts()
    {
        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""{"method":"StartRecording","parameters":{"gameId":"Overwatch"}}""");
        var (started, _) = await host.ReceiveAsyncParsed();
        Assert.Equal("state", started);

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

    // ---- the outbound queue ----

    // A client that has stopped reading used to make every Broadcast queue against it forever: the
    // outbound channel was unbounded, so one wedged connection grew the host's memory without limit
    // for as long as the app kept pushing. Nothing here touches the socket — the queue fills because
    // no writer is draining it, which is exactly the wedged client's situation.
    [Fact]
    public void AClientThatIsNotRead_DropsFramesRatherThanQueueingThemForever()
    {
        var connection = new IpcServer.ClientConnection(new NeverReadSocket(), new IpcServer(null!, new SessionToken()));

        for (var index = 0; index < 4000; index++)
            connection.Send("{\"method\":\"state\",\"content\":{}}");

        Assert.True(connection.DroppedFrames > 0,
            "an outbound queue nothing is draining must drop frames, not grow without limit");
    }

    // The connection only stores the socket in this test; nothing is sent or received on it.
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

    // Reads until the server's close frame arrives, ignoring anything it pushed first.
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
