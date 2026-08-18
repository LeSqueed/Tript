// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Xunit;

namespace Tript.App.Tests;

// The per-launch session token. The Origin check on the control socket stops a malicious web page;
// it cannot stop another user's process on the same machine (loopback is not user-scoped, and a
// non-browser client sends no Origin at all), a browser extension with host permissions, or
// anything whatsoever on the content server — which by design checks no Origin, because a <video>
// element sends none. The token is what closes those, on all three listeners at once.
//
// IpcOriginTests covers the Origin allowlist itself; this covers the token, including the property
// that matters most: the token is required IN ADDITION to the Origin check, never instead of it.
[Collection(AppHostCollection.Name)]
public sealed class SessionTokenTests : IDisposable
{
    private readonly AppHostCollectionFixture _fixture;
    private readonly string _contentRoot;
    private readonly string _settingsPath;
    private readonly string _webRoot;

    // A token of the right shape but not this launch's, and one of the wrong length. Both are
    // refused, and the second is here because a comparison that stops at the first difference or
    // trusts a length prefix would treat it differently from the first.
    private const string WrongToken = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string ShortToken = "0123";

    public SessionTokenTests(AppHostCollectionFixture fixture)
    {
        _fixture = fixture;
        _contentRoot = fixture.NewContentRoot(nameof(SessionTokenTests));
        _settingsPath = fixture.NewSettingsPath(nameof(SessionTokenTests));

        // The UI host serves whatever is in its web root; the suite runs from a tree where the
        // built frontend is not next to the test assembly, so a two-file root stands in for it.
        _webRoot = Path.Combine(_contentRoot, "web");
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><title>Tript</title>");
        File.WriteAllText(Path.Combine(_webRoot, "app.js"), "export const ok = 1;");
    }

    public void Dispose()
    {
    }

    // ---- the UI host ----

    [Fact]
    public async Task The_ui_host_serves_nothing_without_the_token()
    {
        var host = Start();
        await using var _ = host;

        foreach (var query in new[] { "", $"?k={WrongToken}", $"?k={ShortToken}", "?k=" })
        {
            var (status, body) = await GetAsync($"http://localhost:{LocalPorts.Ui}/{query}");
            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.DoesNotContain("<!doctype html>", body, StringComparison.OrdinalIgnoreCase);
        }

        // An asset is gated too: an ungated bundle is the SPA's whole behaviour served anyway.
        var (assetStatus, _) = await GetAsync($"http://localhost:{LocalPorts.Ui}/app.js");
        Assert.Equal(HttpStatusCode.Forbidden, assetStatus);

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task The_ui_host_serves_the_spa_with_the_token_and_then_takes_a_cookie()
    {
        var host = Start();
        await using var _ = host;

        var (status, body, setCookie) = await GetWithCookieAsync(host.UiUrl, cookie: null);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("<!doctype html>", body, StringComparison.OrdinalIgnoreCase);

        // The cookie is how the assets the document pulls in are let through without the token
        // being repeated in every URL the page requests.
        Assert.NotNull(setCookie);
        Assert.Contains("HttpOnly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SameSite=Strict", setCookie, StringComparison.OrdinalIgnoreCase);

        var jar = setCookie!.Split(';')[0];
        var (assetStatus, assetBody, _) = await GetWithCookieAsync(
            $"http://localhost:{LocalPorts.Ui}/app.js", jar);
        Assert.Equal(HttpStatusCode.OK, assetStatus);
        Assert.Contains("export const ok", assetBody, StringComparison.Ordinal);

        await host.ShutdownAsync();
    }

    // ---- the content server ----

    [Fact]
    public async Task The_content_server_refuses_a_request_without_the_token()
    {
        var host = Start();
        await using var _ = host;

        var file = Path.Combine(_contentRoot, "sessions", "clip.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "0123456789");

        foreach (var query in new[] { "", $"?k={WrongToken}", $"?k={ShortToken}" })
        {
            var (status, body) = await GetAsync(
                $"http://localhost:{LocalPorts.Content}/api/content/sessions/clip.mp4{query}");
            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.DoesNotContain("0123456789", body, StringComparison.Ordinal);

            var (thumbStatus, _) = await GetAsync(
                $"http://localhost:{LocalPorts.Content}/api/thumbnail/sessions/clip.mp4{query}");
            Assert.Equal(HttpStatusCode.Forbidden, thumbStatus);
        }

        // The same file, with this launch's token, is served — so the refusals above are the token
        // and not a broken path.
        var (ok, served) = await GetAsync(
            host.WithToken($"http://localhost:{LocalPorts.Content}/api/content/sessions/clip.mp4"));
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Equal("0123456789", served);

        await host.ShutdownAsync();
    }

    // ---- the control socket ----

    [Fact]
    public async Task The_control_socket_refuses_a_handshake_without_the_token()
    {
        var host = Start();
        await using var _ = host;

        foreach (var query in new[] { "", $"?k={WrongToken}", $"?k={ShortToken}" })
        {
            var refused = await ConnectAsync($"ws://localhost:{LocalPorts.ControlSocket}/{query}", origin: null);
            Assert.Equal(HttpStatusCode.Forbidden, refused);
        }

        var accepted = await ConnectAsync(
            host.WithToken($"ws://localhost:{LocalPorts.ControlSocket}/"), origin: null);
        Assert.Null(accepted);

        await host.ShutdownAsync();
    }

    // The "in addition, not instead" property. A page on another origin that somehow learned the
    // token — a log the user pasted, a screenshot of the terminal — is still not the app's own UI.
    [Fact]
    public async Task A_correct_token_from_a_foreign_origin_is_still_refused()
    {
        var host = Start();
        await using var _ = host;

        var refused = await ConnectAsync(
            host.WithToken($"ws://localhost:{LocalPorts.ControlSocket}/"), origin: "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, refused);

        // And the other half of "in addition": the app's own origin is not enough on its own.
        var untokened = await ConnectAsync(
            $"ws://localhost:{LocalPorts.ControlSocket}/", origin: $"http://localhost:{LocalPorts.Ui}");
        Assert.Equal(HttpStatusCode.Forbidden, untokened);

        var accepted = await ConnectAsync(
            host.WithToken($"ws://localhost:{LocalPorts.ControlSocket}/"),
            origin: $"http://localhost:{LocalPorts.Ui}");
        Assert.Null(accepted);

        await host.ShutdownAsync();
    }

    // ---- the token itself ----

    [Fact]
    public void The_printed_url_carries_a_token_of_full_length()
    {
        var host = Start();
        using var _ = host;

        Assert.StartsWith($"http://localhost:{LocalPorts.Ui}/?k=", host.UiUrl, StringComparison.Ordinal);

        // 256 bits, hex: anything shorter is guessable by a process that can hammer loopback.
        Assert.Equal(64, host.Token.Length);
        Assert.All(host.Token, c => Assert.True(Uri.IsHexDigit(c), "the token must be URL-safe"));
    }

    // Two launches must not share a token, or a token learned once outlives the launch it belonged
    // to — the whole point of "per launch".
    [Fact]
    public async Task Every_launch_gets_its_own_token()
    {
        string first;
        var host = Start();
        await using (host)
        {
            first = host.Token;
            await host.ShutdownAsync();
        }

        var second = Start();
        await using (second)
        {
            Assert.NotEqual(first, second.Token);
            await second.ShutdownAsync();
        }
    }

    // The comparison is constant-time. Asserted as "this is the API the code uses", not as a timing
    // measurement: a timing assertion on a shared CI runner is a flaky test, not a security test.
    [SkippableFact]
    public void The_token_comparison_uses_a_fixed_time_api()
    {
        var source = RepoFile("src/Tript.App/SessionToken.cs");
        Skip.If(source is null, "the app sources are not next to this test run");

        var text = File.ReadAllText(source!);
        Assert.Contains("CryptographicOperations.FixedTimeEquals", text, StringComparison.Ordinal);
        Assert.DoesNotContain("string.Equals", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SequenceEqual", text, StringComparison.Ordinal);
    }

    // ---- helpers ----

    private AppHostDriver Start() =>
        AppHostDriver.StartFake(_contentRoot, _settingsPath, gameListJson: null, webRoot: _webRoot);

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(string url)
    {
        var (status, body, _) = await GetWithCookieAsync(url, cookie: null);
        return (status, body);
    }

    // A raw socket rather than HttpClient: the tests care about the exact status and the Set-Cookie
    // header, and HttpClient's cookie container would quietly re-send a cookie between cases.
    private static async Task<(HttpStatusCode Status, string Body, string? SetCookie)> GetWithCookieAsync(
        string url, string? cookie)
    {
        var uri = new Uri(url);
        using var client = new TcpClient();
        await client.ConnectAsync(uri.Host, uri.Port);
        await using var stream = client.GetStream();

        var request = new StringBuilder()
            .Append($"GET {uri.PathAndQuery} HTTP/1.1\r\n")
            .Append($"Host: {uri.Host}:{uri.Port}\r\n");
        if (cookie is not null)
            request.Append($"Cookie: {cookie}\r\n");
        request.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()));

        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
            ms.Write(buffer, 0, read);

        var text = Encoding.UTF8.GetString(ms.ToArray());
        var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = split < 0 ? text : text[..split];
        var body = split < 0 ? string.Empty : text[(split + 4)..];

        var lines = head.Split("\r\n");
        var status = (HttpStatusCode)int.Parse(lines[0].Split(' ')[1]);
        var setCookie = lines.Skip(1)
            .FirstOrDefault(line => line.StartsWith("Set-Cookie:", StringComparison.OrdinalIgnoreCase))
            ?["Set-Cookie:".Length..].Trim();

        return (status, body, setCookie);
    }

    // Connects a WebSocket and returns the status the handshake was refused with, or null when it
    // was accepted.
    private static async Task<HttpStatusCode?> ConnectAsync(string url, string? origin)
    {
        using var socket = new ClientWebSocket();
        if (origin is not null)
            socket.Options.SetRequestHeader("Origin", origin);

        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(new Uri(url), deadline.Token);
            return null;
        }
        catch (WebSocketException exception)
        {
            return exception.Message.Contains("403", StringComparison.Ordinal)
                ? HttpStatusCode.Forbidden
                : throw new Xunit.Sdk.XunitException($"the handshake failed for another reason: {exception.Message}");
        }
    }

    private static string? RepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(typeof(SessionTokenTests).Assembly.Location)!);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        return null;
    }
}
