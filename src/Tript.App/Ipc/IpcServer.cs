// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

using Serilog;

using Tript.App;

namespace Tript.App.Ipc;

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

    private readonly CancellationToken _shutdown;

    private Task? _acceptLoop;
    private volatile bool _running;
    private int _shutdownRequested;
    private int _disposed;

    public IpcServer(AppController controller, SessionToken token, int port = LocalPorts.ControlSocket,
        int uiPort = LocalPorts.Ui)
    {
        _controller = controller;
        _token = token;
        _port = port;
        _allowedOrigins = LocalPorts.OriginsFor(uiPort);
        _shutdown = _cts.Token;
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
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (_running)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    continue;
                }

                if (!IsOriginAllowed(context.Request.Headers["Origin"]))
                {
                    context.Response.StatusCode = 403;
                    context.Response.Close();
                    continue;
                }

                if (!_token.Authorises(context.Request))
                {
                    context.Response.StatusCode = 403;
                    context.Response.Close();
                    continue;
                }

                var wsContext = await context.AcceptWebSocketAsync(null).WaitAsync(_shutdown);
                var client = new ClientConnection(wsContext.WebSocket, this);
                var accepted = false;
                lock (_gate)
                {
                    if (_running)
                    {
                        _clients.Add(client);
                        accepted = true;
                    }
                }

                if (accepted)
                    client.Start();
                else
                    client.Dispose();
            }
            catch (HttpListenerException)
            {
                if (_running)
                    continue;
                break;
            }
            catch (WebSocketException)
            {
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception exception)
            {
                if (_running)
                    Log.Warning(exception, "Ipc: admitting a client failed");
            }
        }
    }

    private bool IsOriginAllowed(string? origin) => IsAllowedOrigin(origin, _allowedOrigins);

    internal static bool IsAllowedOrigin(string? origin) => IsAllowedOrigin(origin, LocalPorts.UiOrigins);

    internal static bool IsAllowedOrigin(string? origin, IReadOnlyCollection<string> allowed) =>
        string.IsNullOrEmpty(origin) || allowed.Contains(origin, StringComparer.OrdinalIgnoreCase);

    internal async Task DispatchAsync(ClientConnection client, string method, JsonElement? parameters)
    {
        Log.Debug("Ipc: dispatching {Method}", Loggable(method));
        try
        {
            var handle = new ClientHandle((message, content) => client.Send(Serialize(message, content)));
            await _controller.HandleAsync(method, parameters, handle);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Ipc: command {Method} failed", Loggable(method));

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
        }
    }

    private static string Loggable(string method)
    {
        var trimmed = method.Length <= 64 ? method : method[..64];
        return new string(Array.ConvertAll(trimmed.ToCharArray(),
            c => char.IsControl(c) ? '?' : c));
    }

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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

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
        }

        _acceptLoop?.Wait(TimeSpan.FromSeconds(2));
        _cts.Dispose();
    }

    internal sealed class ClientConnection : IDisposable
    {
        internal const int MaxInboundMessageBytes = 256 * 1024;

        private const int OutboundCapacity = 256;

        private readonly WebSocket _socket;
        private readonly IpcServer _owner;
        private readonly Channel<byte[]> _outbound;
        private readonly CancellationTokenSource _cts = new();
        private readonly CancellationToken _closed;
        private int _dropped;
        private int _disposed;

        internal int DroppedFrames => Volatile.Read(ref _dropped);

        internal ClientConnection(WebSocket socket, IpcServer owner)
        {
            _socket = socket;
            _owner = owner;
            _closed = _cts.Token;

            _outbound = Channel.CreateBounded<byte[]>(
                new BoundedChannelOptions(OutboundCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                },
                _ =>
                {
                    if (Interlocked.Increment(ref _dropped) == 1)
                        Log.Warning("Ipc: a client is not reading; its oldest queued frames are being dropped.");
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
                await foreach (var frame in _outbound.Reader.ReadAllAsync(_closed))
                {
                    await _socket.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Text,
                        endOfMessage: true, _closed);
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or ObjectDisposedException)
            {
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "Ipc: a client writer stopped unexpectedly");
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
                        result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _closed);
                        if (result.MessageType == WebSocketMessageType.Close)
                            return;
                        if (result.MessageType == WebSocketMessageType.Binary)
                            return;

                        if (ms.Length + result.Count > MaxInboundMessageBytes)
                        {
                            Log.Warning("Ipc: a client sent a message over {Limit} bytes; closing it.",
                                MaxInboundMessageBytes);
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
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "Ipc: a client receive loop stopped unexpectedly");
            }
            finally
            {
                _owner.RemoveClient(this);
                _outbound.Writer.TryComplete();

                // "The UI went blank" is usually this socket closing, and nothing recorded when or why.
                Log.Debug("Ipc: a client disconnected (state {State}, close status {Status})",
                    _socket.State, _socket.CloseStatus?.ToString() ?? "none");

                // Dispose, not just the socket: the connection's CancellationTokenSource was otherwise
                // never released, one per UI reload for the life of the process.
                try
                {
                    Dispose();
                }
                catch (Exception exception)
                {
                    Log.Debug(exception, "Ipc: releasing a closed client connection failed");
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
            }
        }

        internal void Send(string frame) => _outbound.Writer.TryWrite(Encoding.UTF8.GetBytes(frame));

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
            catch (JsonException exception)
            {
                // The only client is Tript's own UI, so this is a frontend bug, not noise.
                Log.Warning(exception, "Ipc: a client sent a message that is not valid JSON; it was ignored");
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _cts.Cancel();
            try
            {
                _socket.Abort();
            }
            catch
            {
            }

            _socket.Dispose();

            _cts.Dispose();
        }
    }
}
