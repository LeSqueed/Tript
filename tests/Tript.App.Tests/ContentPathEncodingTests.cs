// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Tript.App.Tests;

[Collection(AppHostCollection.Name)]
public sealed class ContentPathEncodingTests
{
    private readonly string _contentRoot;
    private readonly string _settingsPath;

    public ContentPathEncodingTests(AppHostCollectionFixture fixture)
    {
        _contentRoot = fixture.NewContentRoot(nameof(ContentPathEncodingTests));
        _settingsPath = fixture.NewSettingsPath(nameof(ContentPathEncodingTests));
    }

    [SkippableTheory]
    [InlineData("my clip.mp4", "my%20clip.mp4")]
    [InlineData("my#clip.mp4", "my%23clip.mp4")]
    [InlineData("what?.mp4", "what%3F.mp4")]
    [InlineData("100% real.mp4", "100%25%20real.mp4")]
    public async Task AnEscapedFileName_IsServed(string fileName, string escaped)
    {
        if (fileName.Any(c => Path.GetInvalidFileNameChars().Contains(c)))
            throw new Xunit.SkipException("the file name is not representable on this platform");

        const string payload = "0123456789";
        Write($"sessions/{fileName}", payload);

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;

        var (status, body) = await GetRawAsync(host, $"/api/content/sessions/{escaped}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(payload, body);

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task AnEscapedFileName_ServesItsCachedThumbnail()
    {
        Write("sessions/my clip.mp4", "video");

        var cached = Path.Combine(_contentRoot, "metadata", "thumbnails", "my clip.mp4.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
        await File.WriteAllTextAsync(cached, "JPEGBYTES");
        await File.WriteAllTextAsync($"{cached}.version", "2");
        File.SetLastWriteTimeUtc(cached, DateTime.UtcNow.AddMinutes(5));

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;

        var (status, body) = await GetRawAsync(host, "/api/thumbnail/sessions/my%20clip.mp4");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("JPEGBYTES", body);

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task AFileNameContainingTwoDots_IsServed()
    {
        const string payload = "not a traversal";
        Write("sessions/my..clip.mp4", payload);

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;

        var (status, body) = await GetRawAsync(host, "/api/content/sessions/my..clip.mp4");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(payload, body);

        await host.ShutdownAsync();
    }

    [Theory]
    [InlineData("/api/content/%2e%2e/sentinel/secret.txt")]
    [InlineData("/api/content/%2E%2E/sentinel/secret.txt")]
    [InlineData("/api/content/sessions/%2e%2e%2f%2e%2e%2fsentinel/secret.txt")]
    [InlineData("/api/content/%2e%2e%5csentinel%5csecret.txt")]
    [InlineData("/api/content/../sentinel/secret.txt")]
    [InlineData("/api/content/sessions/../../sentinel/secret.txt")]
    [InlineData("/api/thumbnail/%2e%2e/sentinel/secret.txt")]
    [InlineData("/api/thumbnail/../sentinel/secret.txt")]
    [InlineData("/api/thumbnail/sessions/../../../sentinel/secret.txt")]
    public async Task AnEncodedTraversal_IsStillRefused(string rawPath)
    {
        var outside = Path.Combine(Path.GetTempPath(), "tript-app-tests", "sentinel", "secret.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        await File.WriteAllTextAsync(outside, "TOP SECRET");

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;

        var (status, body) = await GetRawAsync(host, rawPath);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.DoesNotContain("TOP SECRET", body, StringComparison.Ordinal);
        Assert.Equal("TOP SECRET", await File.ReadAllTextAsync(outside));

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task ADoubleEncodedTraversal_IsNotServed()
    {
        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;

        var (status, _) = await GetRawAsync(host, "/api/content/%252e%252e/sentinel/secret.txt");
        Assert.True(status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
            $"a double-encoded traversal must not be served; it answered {status}");

        await host.ShutdownAsync();
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_contentRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static async Task<(HttpStatusCode Status, string Body)> GetRawAsync(AppHostDriver host, string rawPath)
    {
        rawPath = host.WithToken(rawPath);
        using var client = new TcpClient();
        await client.ConnectAsync("localhost", host.ContentPort);
        await using var stream = client.GetStream();

        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"GET {rawPath} HTTP/1.1\r\nHost: localhost:{host.ContentPort}\r\nConnection: close\r\n\r\n"));

        using var response = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
            response.Write(buffer, 0, read);

        var text = Encoding.UTF8.GetString(response.ToArray());
        var status = (HttpStatusCode)int.Parse(text.Split('\r', '\n')[0].Split(' ')[1]);
        var separator = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return (status, separator >= 0 ? text[(separator + 4)..] : string.Empty);
    }
}
