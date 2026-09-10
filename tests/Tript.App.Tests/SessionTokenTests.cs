// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Xunit;

namespace Tript.App.Tests;

[Collection(AppHostCollection.Name)]
public sealed class SessionTokenTests : IDisposable
{
    private readonly AppHostCollectionFixture _fixture;
    private readonly string _contentRoot;
    private readonly string _settingsPath;
    private readonly string _webRoot;

    private const string WrongToken = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string ShortToken = "0123";

    public SessionTokenTests(AppHostCollectionFixture fixture)
    {
        _fixture = fixture;
        _contentRoot = fixture.NewContentRoot(nameof(SessionTokenTests));
        _settingsPath = fixture.NewSettingsPath(nameof(SessionTokenTests));

        _webRoot = Path.Combine(_contentRoot, "web");
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><title>Tript</title>");
        File.WriteAllText(Path.Combine(_webRoot, "app.js"), "export const ok = 1;");
    }

    public void Dispose()
    {
    }

    [Fact]
    public async Task The_ui_host_serves_nothing_without_the_token()
    {
        var host = Start();
        await using var _ = host;

        foreach (var query in new[] { "", $"?k={WrongToken}", $"?k={ShortToken}", "?k=" })
        {
            var (status, body) = await GetAsync($"http://localhost:{host.UiPort}/{query}");
            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.DoesNotContain("<!doctype html>", body, StringComparison.OrdinalIgnoreCase);
        }

        var (assetStatus, _) = await GetAsync($"http://localhost:{host.UiPort}/app.js");
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

        Assert.NotNull(setCookie);
        Assert.Contains("HttpOnly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SameSite=Strict", setCookie, StringComparison.OrdinalIgnoreCase);

        var jar = setCookie!.Split(';')[0];
        var (assetStatus, assetBody, _) = await GetWithCookieAsync(
            $"http://localhost:{host.UiPort}/app.js", jar);
        Assert.Equal(HttpStatusCode.OK, assetStatus);
        Assert.Contains("export const ok", assetBody, StringComparison.Ordinal);

        var (referrerStatus, referrerBody, _) = await GetWithCookieAsync(
            $"http://localhost:{host.UiPort}/app.js", cookie: null,
            referrer: host.UiUrl + "#library");
        Assert.Equal(HttpStatusCode.OK, referrerStatus);
        Assert.Contains("export const ok", referrerBody, StringComparison.Ordinal);

        var (foreignReferrerStatus, _, _) = await GetWithCookieAsync(
            $"http://localhost:{host.UiPort}/app.js", cookie: null,
            referrer: $"https://evil.example/?k={host.Token}");
        Assert.Equal(HttpStatusCode.Forbidden, foreignReferrerStatus);

        await host.ShutdownAsync();
    }

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
                $"http://localhost:{host.ContentPort}/api/content/sessions/clip.mp4{query}");
            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.DoesNotContain("0123456789", body, StringComparison.Ordinal);

            var (thumbStatus, _) = await GetAsync(
                $"http://localhost:{host.ContentPort}/api/thumbnail/sessions/clip.mp4{query}");
            Assert.Equal(HttpStatusCode.Forbidden, thumbStatus);
        }

        var (ok, served) = await GetAsync(
            host.WithToken($"http://localhost:{host.ContentPort}/api/content/sessions/clip.mp4"));
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Equal("0123456789", served);

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task The_control_socket_refuses_a_handshake_without_the_token()
    {
        var host = Start();
        await using var _ = host;

        foreach (var query in new[] { "", $"?k={WrongToken}", $"?k={ShortToken}" })
        {
            var refused = await ConnectAsync($"ws://localhost:{host.ControlPort}/{query}", origin: null);
            Assert.Equal(HttpStatusCode.Forbidden, refused);
        }

        var accepted = await ConnectAsync(
            host.WithToken($"ws://localhost:{host.ControlPort}/"), origin: null);
        Assert.Null(accepted);

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task A_correct_token_from_a_foreign_origin_is_still_refused()
    {
        var host = Start();
        await using var _ = host;

        var refused = await ConnectAsync(
            host.WithToken($"ws://localhost:{host.ControlPort}/"), origin: "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, refused);

        var untokened = await ConnectAsync(
            $"ws://localhost:{host.ControlPort}/", origin: $"http://localhost:{host.UiPort}");
        Assert.Equal(HttpStatusCode.Forbidden, untokened);

        var accepted = await ConnectAsync(
            host.WithToken($"ws://localhost:{host.ControlPort}/"),
            origin: $"http://localhost:{host.UiPort}");
        Assert.Null(accepted);

        await host.ShutdownAsync();
    }

    [Fact]
    public void The_printed_url_carries_a_token_of_full_length()
    {
        var host = Start();
        using var _ = host;

        Assert.StartsWith($"http://localhost:{host.UiPort}/?k=", host.UiUrl, StringComparison.Ordinal);

        Assert.Equal(64, host.Token.Length);
        Assert.All(host.Token, c => Assert.True(Uri.IsHexDigit(c), "the token must be URL-safe"));
    }

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

    private AppHostDriver Start() =>
        AppHostDriver.StartFake(_contentRoot, _settingsPath, gameListJson: null, webRoot: _webRoot);

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(string url)
    {
        var (status, body, _) = await GetWithCookieAsync(url, cookie: null);
        return (status, body);
    }

    private static async Task<(HttpStatusCode Status, string Body, string? SetCookie)> GetWithCookieAsync(
        string url, string? cookie, string? referrer = null)
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
        if (referrer is not null)
            request.Append($"Referer: {referrer}\r\n");
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
