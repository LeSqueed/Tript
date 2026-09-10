// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Tript.App.Tests;

[Collection(AppHostCollection.Name)]
public sealed class ContentServerTests : IDisposable
{
    private readonly AppHostCollectionFixture _fixture;
    private readonly string _contentRoot;
    private readonly string _settingsPath;

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
        const string payload = "0123456789abcdefghij";
        var file = Path.Combine(_contentRoot, "sessions", "clip.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, payload);

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;

        using var response = await GetWithRange(host, "sessions/clip.mp4", "bytes=4-8");
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 4-8/20", response.Content.Headers.GetValues("Content-Range").Single());
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("45678", body);

        using var suffix = await GetWithRange(host, "sessions/clip.mp4", "bytes=-5");
        Assert.Equal(HttpStatusCode.PartialContent, suffix.StatusCode);
        Assert.Equal("bytes 15-19/20", suffix.Content.Headers.GetValues("Content-Range").Single());
        Assert.Equal("fghij", await suffix.Content.ReadAsStringAsync());

        using var oob = await GetWithRange(host, "sessions/clip.mp4", "bytes=200-300");
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, oob.StatusCode);

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Traversal_never_escapes_the_content_root()
    {
        var outside = Path.Combine(Path.GetTempPath(), "tript-app-tests", "sentinel", "secret.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        await File.WriteAllTextAsync(outside, "TOP SECRET");

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;

        using (var raw = await SendRawAsync(host, "/api/content/../sentinel/secret.txt"))
            Assert.Equal(HttpStatusCode.Forbidden, raw.StatusCode);

        using (var encoded = await SendRawAsync(host, "/api/content/%2e%2e/sentinel/secret.txt"))
            Assert.Equal(HttpStatusCode.Forbidden, encoded.StatusCode);

        using (var mid = await SendRawAsync(host, "/api/content/sessions/../../../sentinel/secret.txt"))
            Assert.Equal(HttpStatusCode.Forbidden, mid.StatusCode);

        Assert.Equal("TOP SECRET", await File.ReadAllTextAsync(outside));

        using var missing = await SendRawAsync(host, "/api/content/sessions/nope.mp4");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        await host.ShutdownAsync();
    }

    private static Task<HttpResponseMessage> GetWithRange(AppHostDriver host, string path, string range)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            host.WithToken($"http://localhost:{host.ContentPort}/api/content/{path}"));
        request.Headers.TryAddWithoutValidation("Range", range);
        return SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        using var client = new HttpClient();
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendRawAsync(AppHostDriver host, string rawPath)
    {
        rawPath = host.WithToken(rawPath);
        using var client = new TcpClient();
        await client.ConnectAsync("localhost", host.ContentPort);
        await using var stream = client.GetStream();
        var request = $"GET {rawPath} HTTP/1.1\r\nHost: localhost:{host.ContentPort}\r\nConnection: close\r\n\r\n";
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
