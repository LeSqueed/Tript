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
//
// The protocol the driver speaks is the frontend's (spec/local-ipc.md):
//   out: { method, parameters? }    PascalCase method
//   in:  { method, content }        lowercase method
internal sealed class AppHostDriver : IDisposable, IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task _drain;
    private readonly TaskCompletionSource<string?> _ready = new();
    private ClientWebSocket? _socket;

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
            while (!_process.HasExited || _process.StandardOutput.Peek() >= 0)
            {
                var line = await _process.StandardOutput.ReadLineAsync();
                if (line is null)
                    break;
                if (line.Contains("READY", StringComparison.Ordinal))
                    _ready.TrySetResult(line);
            }
        });
    }

    internal static AppHostDriver StartFake(string contentRoot, string settingsPath, string? gameListJson = null)
        => Start(contentRoot, settingsPath, gameListJson, fake: true);

    // The real recording path: no --fake-recorder, so the host starts libobs, resets video/audio,
    // loads the safe modules, and wires a real ObsRecorderSession. The caller is responsible for
    // ensuring the muxer helper sits next to the app binary first.
    internal static AppHostDriver StartReal(string contentRoot, string settingsPath)
        => Start(contentRoot, settingsPath, gameListJson: null, fake: false);

    private static AppHostDriver Start(string contentRoot, string settingsPath, string? gameListJson, bool fake)
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

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The app host could not be started.");
        var driver = new AppHostDriver(process);

        // The READY line is the single-line contract: the control socket, content server and UI
        // host are all reachable once it appears (spec/recorder.md "Host startup").
        if (!driver.WaitForReady(TimeSpan.FromSeconds(30)))
        {
            var stderr = process.StandardError.ReadToEnd();
            driver.Dispose();
            throw new InvalidOperationException(
                $"The app host never printed READY. stderr:\n{stderr}");
        }

        return driver;
    }

    private bool WaitForReady(TimeSpan timeout) => _ready.Task.Wait(timeout);

    internal async Task ConnectWebSocketAsync()
    {
        _socket = new ClientWebSocket();
        await _socket.ConnectAsync(new Uri("ws://localhost:44030/"), CancellationToken.None);
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
    internal async Task<JsonDocument> ReceiveAsync()
    {
        if (_socket is null)
            throw new InvalidOperationException("Not connected.");
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("The socket closed unexpectedly.");
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return JsonDocument.Parse(Encoding.UTF8.GetString(ms.ToArray()));
    }

    internal async Task<(string Method, JsonElement Content)> ReceiveAsyncParsed()
    {
        using var doc = await ReceiveAsync();
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

        _process.Dispose();
    }
}
