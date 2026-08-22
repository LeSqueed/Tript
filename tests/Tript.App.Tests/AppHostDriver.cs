// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Tript.App.Tests;

// Drives the Tript.App executable as a child process and speaks the WebSocket control socket to it.
// The app host owns the OBS context (when real) and the three local IPC channels; running it as a
// child is the only way to exercise the real startup path, the READY contract, and the full IPC
// surface at once.
internal sealed class AppHostDriver : IDisposable, IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task _drain;
    private readonly Task _stderrDrain;
    private readonly StringBuilder _stderr = new();
    private readonly TaskCompletionSource<string?> _ready = new();
    private ClientWebSocket? _socket;

    // The per-launch session token, read off the READY line. Every request the suite makes to any
    // of the three listeners carries it; a host that never printed one leaves this empty and the
    // tests fail on the 403 rather than hanging.
    private string _token = string.Empty;

    private static readonly string AppHostPath =
        Path.Combine(Path.GetDirectoryName(typeof(AppHostDriver).Assembly.Location)!,
            "Tript.App");

    internal static string AppHostPathForReal => AppHostPath;

    internal static bool AppHostExists => File.Exists(AppHostPath);

    private AppHostDriver(Process process)
    {
        _process = process;
        // Drain stdout continuously so the READY line is captured without deadlocking the child,
        // and so the host's log stays readable if a test fails.
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
        string? webRoot = null)
        => Start(contentRoot, settingsPath, gameListJson, fake: true, webRoot);

    // The real recording path: no --fake-recorder, so the host starts libobs, resets video/audio,
    // loads the safe modules, and wires a real ObsRecorderSession. The caller is responsible for
    // ensuring the muxer helper sits next to the app binary first.
    internal static AppHostDriver StartReal(string contentRoot, string settingsPath)
        => Start(contentRoot, settingsPath, gameListJson: null, fake: false, webRoot: null);

    private static AppHostDriver Start(string contentRoot, string settingsPath, string? gameListJson, bool fake,
        string? webRoot)
    {
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
        if (fake)
            startInfo.ArgumentList.Add("--fake-recorder");
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
        startInfo.ArgumentList.Add(TestPorts.Ui.ToString());
        startInfo.ArgumentList.Add("--content-port");
        startInfo.ArgumentList.Add(TestPorts.Content.ToString());
        startInfo.ArgumentList.Add("--control-port");
        startInfo.ArgumentList.Add(TestPorts.ControlSocket.ToString());

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The app host could not be started.");
        var driver = new AppHostDriver(process);

        // The READY line is the single-line contract: the control socket, content server and UI
        // host are all reachable once it appears.
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

    // The UI URL the host printed, token and all — what the desktop shell loads and what a headless
    // user pastes into a browser.
    internal string UiUrl { get; private set; } = string.Empty;

    internal string Token => _token;

    // Appends the session token to a URL the test is about to request. Every listener wants it in
    // the query string: the control socket because a browser cannot set a handshake header, the
    // content server because that is the only channel a <video src> carries.
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
        await _socket.ConnectAsync(new Uri(WithToken($"ws://localhost:{TestPorts.ControlSocket}/")),
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

    // Receives the next frame, parsed to its root element. The frames are
    // { method, content }; the caller reads method/content from the root.
    //
    // A frame that never arrives is a failure of the host, not a reason to wait forever: without the
    // deadline a test for "this command answers" hangs the whole run instead of going red.
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

    internal async Task<(string Method, JsonElement Content)> ReceiveAsyncParsed(TimeSpan? timeout = null)
    {
        using var doc = await ReceiveAsync(timeout);
        var root = doc.RootElement;
        var method = root.GetProperty("method").GetString()!;
        var content = root.GetProperty("content").Clone();
        return (method, content);
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
            // The socket may already be gone; fall back to a hard kill.
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
                // The ports the host bound are not released until the process is gone; wait so the
                // next test in the serialized collection can bind them.
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
