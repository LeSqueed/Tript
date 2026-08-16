// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Tript.App.Ipc;

// The WebSocket control socket (ws://localhost:44030/, spec/local-ipc.md). Sits on a single
// HttpListener and answers WebSocket upgrade requests; every other path/verb gets 404. Clients are
// tracked so the host can broadcast state/settings/gameList pushes to all of them.
//
// The wire contract:
//   frontend -> backend: { method, parameters? }   PascalCase method, no parameters field when the
//                                                  command has no arguments
//   backend  -> frontend: { method, content }      lowercase method
// Every frame is a notification; commands that logically have a result surface it as an unrelated
// later message (spec/local-ipc.md "No request/response correlation").
//
// Threading: one listener thread accepts and dispatches; each client's receive loop is its own
// thread, and Broadcast walks the client list. A client that has not finished reading when the host
// pushes could be sent-to concurrently — HttpListener's WebSocket is not safe for concurrent sends
// from multiple threads, so Broadcast queues onto each client's send channel and a single writer
// thread per client drains it.
internal sealed class IpcServer : IDisposable
{
    private const int Port = 44030;

    private readonly AppController _controller;
    private readonly HttpListener _listener = new();
    private readonly object _gate = new();
    private readonly List<ClientConnection> _clients = [];
    private readonly CancellationTokenSource _cts = new();

    private Thread? _acceptThread;
    private volatile bool _running;

    public IpcServer(AppController controller)
    {
        _controller = controller;
    }

    public event Action? ShutdownRequested;

    public void RequestShutdown() => ShutdownRequested?.Invoke();

    public void Start()
    {
        lock (_gate)
        {
            if (_running)
                return;

            _listener.Prefixes.Add($"http://localhost:{Port}/");
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

    // ---- dispatch ----

    internal void Dispatch(ClientConnection client, string method, JsonElement? parameters)
    {
        Console.Error.WriteLine($"Tript.App.Ipc: dispatching {method}");
        try
        {
            var handle = new ClientHandle((message, content) => client.Send(Serialize(message, content)));
            _controller.Handle(method, parameters, handle);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Tript.App.Ipc: command {method} failed: {exception.Message}");
        }
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
        private readonly WebSocket _socket;
        private readonly IpcServer _owner;
        private readonly Channel<byte[]> _outbound = Channel.CreateUnbounded<byte[]>();
        private CancellationTokenSource _cts = new();

        internal ClientConnection(WebSocket socket, IpcServer owner)
        {
            _socket = socket;
            _owner = owner;
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
                        ms.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    var text = Encoding.UTF8.GetString(ms.ToArray());
                    ParseAndDispatch(text);
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

        internal void Send(string frame)
        {
            try
            {
                _outbound.Writer.TryWrite(Encoding.UTF8.GetBytes(frame));
            }
            catch (Exception)
            {
                // The channel is unbounded; TryWrite only fails after the writer has completed.
            }
        }

        private void ParseAndDispatch(string text)
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

                _owner.Dispatch(this, method, parameters);
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
