// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

using Tript.App;

namespace Tript.App.Ipc;

// The WebSocket control socket. Sits on a single
// HttpListener and answers WebSocket upgrade requests; every other path/verb gets 404.
internal sealed class IpcServer : IDisposable
{
    private readonly int _port;
    private readonly string[] _allowedOrigins;

    private readonly AppController _controller;
    private readonly SessionToken _token;
    private readonly HttpListener _listener = new();
    private readonly object _gate = new();
    private readonly List<ClientConnection> _clients = [];
    private readonly CancellationTokenSource _cts = new();

    private Thread? _acceptThread;
    private volatile bool _running;
    private int _shutdownRequested;

    public IpcServer(AppController controller, SessionToken token, int port = LocalPorts.ControlSocket,
        int uiPort = LocalPorts.Ui)
    {
        _controller = controller;
        _token = token;
        _port = port;
        _allowedOrigins = [$"http://localhost:{uiPort}", $"http://127.0.0.1:{uiPort}"];
    }

    public event Action? ShutdownRequested;

    internal bool ShutdownWasRequested => Volatile.Read(ref _shutdownRequested) != 0;

    public void RequestShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownRequested, 1) == 0)
            ShutdownRequested?.Invoke();
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running)
                return;

            _listener.Prefixes.Add($"http://localhost:{_port}/");
            _listener.Start();
            _running = true;

            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "Tript.App.Ipc.Accept",
            };
            _acceptThread.Start();
        }
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            try
            {
                var context = _listener.GetContext();
                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    continue;
                }

                // Browsers do not apply CORS to a WebSocket handshake, so without this any page the
                // user happens to have open could drive this socket: delete recordings, empty the
                // trash, move the output directory. The Origin header is the only thing that
                // distinguishes the app's own UI from someone else's page, and a browser will not
                // let script forge it.
                if (!IsOriginAllowed(context.Request.Headers["Origin"]))
                {
                    context.Response.StatusCode = 403;
                    context.Response.Close();
                    continue;
                }

                // The session token, required IN ADDITION to the Origin check and never instead of
                // it. The Origin check answers "is this the app's own page"; it says nothing about
                // a client that presents no Origin at all — another user's process on this machine,
                // or a browser extension with host permissions — and those are exactly what the
                // allowlist has to let through so the app's own webview can connect. It rides in
                // the query string because a browser cannot set a header on a WebSocket handshake.
                // Refused before the upgrade, so nothing is ever dispatched for an unauthorised
                // client.
                if (!_token.Authorises(context.Request))
                {
                    context.Response.StatusCode = 403;
                    context.Response.Close();
                    continue;
                }

                var wsContext = context.AcceptWebSocketAsync(null).GetAwaiter().GetResult();
                var client = new ClientConnection(wsContext.WebSocket, this);
                lock (_gate)
                {
                    _clients.Add(client);
                }
                client.Start();
            }
            catch (HttpListenerException)
            {
                if (_running)
                    continue;
                break;
            }
            catch (WebSocketException)
            {
                // A client that dies mid-handshake; nothing to do.
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    // The UI host's own origin, in both spellings a browser may present for the loopback address.
    private static readonly string[] AllowedOrigins = LocalPorts.UiOrigins;

    private bool IsOriginAllowed(string? origin) =>
        string.IsNullOrEmpty(origin) ||
        _allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);

    internal static bool IsAllowedOrigin(string? origin) =>
        string.IsNullOrEmpty(origin) ||
        AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);

    // ---- dispatch ----

    internal async Task DispatchAsync(ClientConnection client, string method, JsonElement? parameters)
    {
        Console.Error.WriteLine($"Tript.App.Ipc: dispatching {Loggable(method)}");
        try
        {
            var handle = new ClientHandle((message, content) => client.Send(Serialize(message, content)));
            await _controller.HandleAsync(method, parameters, handle);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Tript.App.Ipc: command {Loggable(method)} failed: {exception.Message}");

            // A command that threw produced no frame of its own, so without this the client is left
            // waiting on a reply that never comes and the user sees nothing happen at all. Several
            // commands can throw on input the wire allows, and stderr is not somewhere a user looks.
            TrySendError(client, $"That action could not be completed ({exception.Message}).");
        }
    }

    private void TrySendError(ClientConnection client, string message)
    {
        try
        {
            client.Send(Serialize("error", JsonSerializer.SerializeToElement(new { message }, Wire.Options)));
        }
        catch (Exception exception) when (exception is ObjectDisposedException or WebSocketException
                                             or InvalidOperationException)
        {
            // The client that sent the command has gone. Nothing left to tell.
        }
    }

    // A method name off the wire, bounded and stripped of anything that could forge a line in the
    // log. It is attacker-influenced and reached on every frame.
    private static string Loggable(string method)
    {
        var trimmed = method.Length <= 64 ? method : method[..64];
        return new string(Array.ConvertAll(trimmed.ToCharArray(),
            c => char.IsControl(c) ? '?' : c));
    }

    // ---- broadcast ----

    public void Broadcast(string method, JsonElement content)
    {
        var frame = Serialize(method, content);
        lock (_gate)
        {
            foreach (var client in _clients.ToArray())
                client.Send(frame);
        }
    }

    public void Broadcast(string method, object content) =>
        Broadcast(method, JsonSerializer.SerializeToElement(content, Wire.Options));

    // The envelope on the wire: { method, content }.
    internal static string Serialize(string method, JsonElement content)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("method", method);
            writer.WritePropertyName("content");
            content.WriteTo(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal void RemoveClient(ClientConnection client)
    {
        lock (_gate)
        {
            _clients.Remove(client);
        }
    }

    public void Dispose()
    {
        _running = false;
        _cts.Cancel();

        ClientConnection[] clients;
        lock (_gate)
        {
            clients = _clients.ToArray();
            _clients.Clear();
        }

        foreach (var client in clients)
            client.Dispose();

        try
        {
            _listener.Stop();
        }
        catch
        {
        }

        try
        {
            _listener.Close();
        }
        catch
        {
            // A Close racing the listener's own teardown can throw (HttpListenerException when the
            // endpoint is already gone); the process is exiting, so a close failure is not worth
            // propagating into Program.Main's exit code.
        }
    }

    // ---- client ----

    internal sealed class ClientConnection : IDisposable
    {
        // The largest command this protocol can carry. Commands are small JSON objects; the biggest
        // real one is a clip request with its segment list. The receive loop accumulated
        // continuation frames with no ceiling at all and then doubled the peak with ToArray(), so a
        // single client message of arbitrary length was an unbounded allocation in this process.
        internal const int MaxInboundMessageBytes = 256 * 1024;

        // How many outgoing frames may queue for a client that has stopped reading. Unbounded meant
        // one such client made every Broadcast queue forever.
        private const int OutboundCapacity = 256;

        private readonly WebSocket _socket;
        private readonly IpcServer _owner;
        private readonly Channel<byte[]> _outbound;
        private CancellationTokenSource _cts = new();
        private int _dropped;

        // How many outgoing frames this connection has thrown away because it was not being read.
        internal int DroppedFrames => Volatile.Read(ref _dropped);

        internal ClientConnection(WebSocket socket, IpcServer owner)
        {
            _socket = socket;
            _owner = owner;

            // DropOldest, not DropWrite: every push in this app is a FULL push, so the newest frame
            // supersedes every older one and dropping from the front leaves the client converging on
            // the current truth. DropWrite would do the opposite — discard the newest and leave a
            // stale view pinned forever. The cost is that a one-shot frame (an error toast, an
            // intermediate importProgress tick) can be lost by a client that is this far behind;
            // that is the right trade against a queue that grows without limit. Waiting is not an
            // option: Send runs under the broadcast gate, so a blocked write would stall the caller
            // and every other client with it.
            _outbound = Channel.CreateBounded<byte[]>(
                new BoundedChannelOptions(OutboundCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                },
                _ =>
                {
                    if (Interlocked.Increment(ref _dropped) == 1)
                        Console.Error.WriteLine(
                            "Tript.App.Ipc: a client is not reading; its oldest queued frames are being dropped.");
                });
        }

        internal void Start()
        {
            _ = RunWriterAsync();
            _ = RunReceiveAsync();
        }

        private async Task RunWriterAsync()
        {
            try
            {
                await foreach (var frame in _outbound.Reader.ReadAllAsync(_cts.Token))
                {
                    await _socket.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Text,
                        endOfMessage: true, _cts.Token);
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or ObjectDisposedException)
            {
                // A cancelled/closed client ends the writer; ReceiveLoop notices the close.
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Tript.App.Ipc: writer died: {exception.Message}");
            }
        }

        private async Task RunReceiveAsync()
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (_socket.State == WebSocketState.Open)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                            return;
                        if (result.MessageType == WebSocketMessageType.Binary)
                            return;

                        if (ms.Length + result.Count > MaxInboundMessageBytes)
                        {
                            // Refusing the rest of the message would leave the connection out of step
                            // with a sender that is still writing it, so the connection goes.
                            Console.Error.WriteLine(
                                $"Tript.App.Ipc: a client sent a message over {MaxInboundMessageBytes} bytes; closing it.");
                            await CloseTooBigAsync();
                            return;
                        }

                        ms.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    var text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                    await ParseAndDispatchAsync(text);
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or ObjectDisposedException)
            {
                // A dead peer; nothing to clean up but the client itself.
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Tript.App.Ipc: receive loop died: {exception.Message}");
            }
            finally
            {
                _owner.RemoveClient(this);
                _outbound.Writer.TryComplete();
                try
                {
                    _socket.Dispose();
                }
                catch
                {
                }
            }
        }

        private async Task CloseTooBigAsync()
        {
            try
            {
                await _socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig,
                    "message too large", CancellationToken.None);
            }
            catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException
                                                 or OperationCanceledException)
            {
                // The peer is already gone; the finally below tears the connection down anyway.
            }
        }

        internal void Send(string frame)
        {
            try
            {
                // DropOldest, so TryWrite makes room rather than refusing; it only returns false once
                // the writer has completed, which is the connection already going away.
                _outbound.Writer.TryWrite(Encoding.UTF8.GetBytes(frame));
            }
            catch (Exception)
            {
                // A frame for a client that is already gone. Broadcast holds the client list gate
                // while it calls this, so throwing here would take every other client down with it.
            }
        }

        private async Task ParseAndDispatchAsync(string text)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return;
                if (!root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
                    return;

                var method = methodElement.GetString();
                if (string.IsNullOrEmpty(method))
                    return;

                JsonElement? parameters = null;
                if (root.TryGetProperty("parameters", out var parametersElement))
                    parameters = parametersElement;

                await _owner.DispatchAsync(this, method, parameters);
            }
            catch (JsonException)
            {
                // A malformed frame is dropped; the connection stays up.
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _socket.Abort();
            }
            catch
            {
            }

            _socket.Dispose();
        }
    }
}
