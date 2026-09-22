// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Tript.App.Tests;

internal sealed class AppHostDriver : IDisposable, IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task _drain;
    private readonly Task _stderrDrain;
    private readonly StringBuilder _stderr = new();
    private readonly TaskCompletionSource<string?> _ready = new();
    private ClientWebSocket? _socket;

    internal int UiPort { get; }

    internal int ContentPort { get; }

    internal int ControlPort { get; }

    internal static int AllocateFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private string _token = string.Empty;

    private static readonly string AppHostPath =
        Path.Combine(Path.GetDirectoryName(typeof(AppHostDriver).Assembly.Location)!,
            "Tript.App");

    internal static string AppHostPathForReal => AppHostPath;

    internal static bool AppHostExists => File.Exists(AppHostPath);

    private AppHostDriver(Process process, int uiPort, int contentPort, int controlPort)
    {
        _process = process;
        UiPort = uiPort;
        ContentPort = contentPort;
        ControlPort = controlPort;

        _drain = Task.Run(async () =>
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.Contains("READY", StringComparison.Ordinal))
                {
                    UiUrl = line[line.IndexOf("READY", StringComparison.Ordinal)..]
                        .Split(' ', 2) is [_, var url] ? url.Trim() : string.Empty;
                    _token = TokenFrom(UiUrl);
                    _ready.TrySetResult(line);
                }
            }
        });
        _stderrDrain = Task.Run(async () =>
        {
            var text = await _process.StandardError.ReadToEndAsync();
            lock (_stderr)
                _stderr.Append(text);
        });
    }

    internal static AppHostDriver StartFake(string contentRoot, string settingsPath, string? gameListJson = null,
        string? webRoot = null, string? fakeRecorderSettingsTrace = null)
        => Start(contentRoot, settingsPath, gameListJson, fake: true, webRoot, fakeRecorderSettingsTrace);

    internal static AppHostDriver StartReal(string contentRoot, string settingsPath)
        => Start(contentRoot, settingsPath, gameListJson: null, fake: false, webRoot: null,
            fakeRecorderSettingsTrace: null);

    private static AppHostDriver Start(string contentRoot, string settingsPath, string? gameListJson, bool fake,
        string? webRoot, string? fakeRecorderSettingsTrace)
    {
        var uiPort = AllocateFreePort();
        var contentPort = AllocateFreePort();
        while (contentPort == uiPort)
            contentPort = AllocateFreePort();
        var controlPort = AllocateFreePort();
        while (controlPort == uiPort || controlPort == contentPort)
            controlPort = AllocateFreePort();

        var startInfo = new ProcessStartInfo
        {
            FileName = AppHostPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--content-root");
        startInfo.ArgumentList.Add(contentRoot);
        startInfo.ArgumentList.Add("--settings-path");
        startInfo.ArgumentList.Add(settingsPath);

        // Without this every child app host wrote into the real user's log folder, and with its
        // ten-file retention each test run deleted one of that user's real logs.
        startInfo.ArgumentList.Add("--log-dir");
        startInfo.ArgumentList.Add(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!, "logs"));
        if (fake)
            startInfo.ArgumentList.Add("--fake-recorder");
        startInfo.ArgumentList.Add("--disable-updater");
        if (fakeRecorderSettingsTrace is not null)
            startInfo.Environment[FakeRecorderSession.SettingsTraceEnvironmentVariable] = fakeRecorderSettingsTrace;
        if (gameListJson is not null)
        {
            startInfo.ArgumentList.Add("--game-list");
            startInfo.ArgumentList.Add(gameListJson);
        }
        if (webRoot is not null)
        {
            startInfo.ArgumentList.Add("--web-root");
            startInfo.ArgumentList.Add(webRoot);
        }
        startInfo.ArgumentList.Add("--ui-port");
        startInfo.ArgumentList.Add(uiPort.ToString());
        startInfo.ArgumentList.Add("--content-port");
        startInfo.ArgumentList.Add(contentPort.ToString());
        startInfo.ArgumentList.Add("--control-port");
        startInfo.ArgumentList.Add(controlPort.ToString());

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The app host could not be started.");
        var driver = new AppHostDriver(process, uiPort, contentPort, controlPort);

        if (!driver.WaitForReady(TimeSpan.FromSeconds(30)))
        {
            string stderr;
            lock (driver._stderr)
                stderr = driver._stderr.ToString();
            driver.Dispose();
            throw new InvalidOperationException(
                $"The app host never printed READY. stderr:\n{stderr}");
        }

        return driver;
    }

    private bool WaitForReady(TimeSpan timeout) => _ready.Task.Wait(timeout);

    internal string UiUrl { get; private set; } = string.Empty;

    internal string Token => _token;

    internal string WithToken(string url) =>
        url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "k=" + _token;

    private static string TokenFrom(string url)
    {
        var query = url.IndexOf('?', StringComparison.Ordinal);
        if (query < 0)
            return string.Empty;

        foreach (var pair in url[(query + 1)..].Split('&'))
        {
            if (pair.StartsWith("k=", StringComparison.Ordinal))
                return pair[2..];
        }

        return string.Empty;
    }

    internal async Task ConnectWebSocketAsync()
    {
        _socket = new ClientWebSocket();
        await _socket.ConnectAsync(new Uri(WithToken($"ws://localhost:{ControlPort}/")),
            CancellationToken.None);
        await SendAsync("""{"method":"NewConnection","parameters":{"protocolVersion":1}}""");
    }

    internal Task SendAsync(string json)
    {
        if (_socket is null)
            throw new InvalidOperationException("Not connected.");
        return _socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text,
            endOfMessage: true, CancellationToken.None);
    }

    internal async Task<JsonDocument> ReceiveAsync(TimeSpan? timeout = null)
    {
        if (_socket is null)
            throw new InvalidOperationException("Not connected.");
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            try
            {
                result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("The host sent no frame within the deadline.");
            }
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("The socket closed unexpectedly.");
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return JsonDocument.Parse(Encoding.UTF8.GetString(ms.ToArray()));
    }

    internal Task<(string Method, JsonElement Content)> ReceiveAsyncParsed(TimeSpan? timeout = null) =>
        ReceiveNextParsed(timeout, skipStorageReports: true);

    internal Task<(string Method, JsonElement Content)> ReceiveAnyParsed(TimeSpan? timeout = null) =>
        ReceiveNextParsed(timeout, skipStorageReports: false);

    private async Task<(string Method, JsonElement Content)> ReceiveNextParsed(TimeSpan? timeout,
        bool skipStorageReports)
    {
        while (true)
        {
            using var doc = await ReceiveAsync(timeout);
            var root = doc.RootElement;
            var method = root.GetProperty("method").GetString()!;
            if (skipStorageReports && method == "storageReport")
                continue;

            return (method, root.GetProperty("content").Clone());
        }
    }

    internal async Task ShutdownAsync()
    {
        try
        {
            await SendAsync("""{"method":"Shutdown"}""");
            if (!_process.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(TimeSpan.FromSeconds(10));
            }
        }
        catch
        {
            if (!_process.HasExited)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(TimeSpan.FromSeconds(10));
                }
                catch
                {
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync();
        Dispose();
    }

    public void Dispose()
    {
        try
        {
            _socket?.Dispose();
        }
        catch
        {
        }

        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);

                _process.WaitForExit(TimeSpan.FromSeconds(10));
            }
            catch
            {
            }
        }

        _drain.Wait(TimeSpan.FromSeconds(2));
        _stderrDrain.Wait(TimeSpan.FromSeconds(2));
        _process.Dispose();
    }
}
