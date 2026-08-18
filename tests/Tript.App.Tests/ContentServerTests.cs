// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Tript.App.Tests;

// The content server (http://localhost:2222/) serves the recorded files with range-request support
// and refuses any path that escapes the content root. These tests hit the running app host over
// HTTP, the same way the frontend does.
[Collection(AppHostCollection.Name)]
public sealed class ContentServerTests : IDisposable
{
    private readonly AppHostCollectionFixture _fixture;
    private readonly string _contentRoot;
    private readonly string _settingsPath;
    private const string Base = "http://localhost:2222";

    public ContentServerTests(AppHostCollectionFixture fixture)
    {
        _fixture = fixture;
        _contentRoot = fixture.NewContentRoot(nameof(ContentServerTests));
        _settingsPath = fixture.NewSettingsPath(nameof(ContentServerTests));
    }

    public void Dispose()
    {
    }

    [Fact]
    public async Task Range_request_serves_the_right_bytes()
    {
        // A 20-byte payload so ranges are predictable.
        const string payload = "0123456789abcdefghij";
        var file = Path.Combine(_contentRoot, "sessions", "clip.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, payload);

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;

        // A range in the middle: bytes 4..8 -> "45678".
        using var response = await GetWithRange(host, "sessions/clip.mp4", "bytes=4-8");
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 4-8/20", response.Content.Headers.GetValues("Content-Range").Single());
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("45678", body);

        // A suffix range: the final 5 bytes -> "fghij".
        using var suffix = await GetWithRange(host, "sessions/clip.mp4", "bytes=-5");
        Assert.Equal(HttpStatusCode.PartialContent, suffix.StatusCode);
        Assert.Equal("bytes 15-19/20", suffix.Content.Headers.GetValues("Content-Range").Single());
        Assert.Equal("fghij", await suffix.Content.ReadAsStringAsync());

        // An out-of-bounds range -> 416.
        using var oob = await GetWithRange(host, "sessions/clip.mp4", "bytes=200-300");
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, oob.StatusCode);

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Traversal_never_escapes_the_content_root()
    {
        // A sentinel outside the content root that a successful traversal would return.
        var outside = Path.Combine(Path.GetTempPath(), "tript-app-tests", "sentinel", "secret.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        await File.WriteAllTextAsync(outside, "TOP SECRET");

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;

        // The raw request must be sent verbatim: HttpClient normalizes ".." before it reaches the
        // server, which would never exercise the guard. A raw socket sends the literal request line
        // so the server's own RawUrl check is what the traversal hits.
        using (var raw = await SendRawAsync(host, "/api/content/../sentinel/secret.txt"))
            Assert.Equal(HttpStatusCode.Forbidden, raw.StatusCode);

        using (var encoded = await SendRawAsync(host, "/api/content/%2e%2e/sentinel/secret.txt"))
            Assert.Equal(HttpStatusCode.Forbidden, encoded.StatusCode);

        using (var mid = await SendRawAsync(host, "/api/content/sessions/../../../sentinel/secret.txt"))
            Assert.Equal(HttpStatusCode.Forbidden, mid.StatusCode);

        // The sentinel must never have been served.
        Assert.Equal("TOP SECRET", await File.ReadAllTextAsync(outside));

        // A missing file inside the root is a clean 404, not a 403 or a leak.
        using var missing = await SendRawAsync(host, "/api/content/sessions/nope.mp4");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        await host.ShutdownAsync();
    }

    // Every request carries the launch's session token: the content server serves nothing without
    // it (SessionTokenTests covers the refusals).
    private static Task<HttpResponseMessage> GetWithRange(AppHostDriver host, string path, string range)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, host.WithToken($"{Base}/api/content/{path}"));
        request.Headers.TryAddWithoutValidation("Range", range);
        return SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        using var client = new HttpClient();
        return await client.SendAsync(request);
    }

    // Sends a raw HTTP/1.1 GET with the literal request path, bypassing HttpClient's URI
    // normalization, so the server's path-traversal guard is actually exercised.
    private static async Task<HttpResponseMessage> SendRawAsync(AppHostDriver host, string rawPath)
    {
        rawPath = host.WithToken(rawPath);
        using var client = new TcpClient();
        await client.ConnectAsync("localhost", 2222);
        await using var stream = client.GetStream();
        var request = $"GET {rawPath} HTTP/1.1\r\nHost: localhost:2222\r\nConnection: close\r\n\r\n";
        var bytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(bytes);

        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
            ms.Write(buffer, 0, read);

        var responseText = Encoding.ASCII.GetString(ms.ToArray());
        var statusLine = responseText.Split('\r', '\n').First();
        var code = int.Parse(statusLine.Split(' ')[1]);

        var response = new HttpResponseMessage((HttpStatusCode)code);
        return response;
    }
}
