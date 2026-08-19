// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Tript.App.Content;
using Tript.Media;
using Xunit;
using Xunit.Sdk;

namespace Tript.App.Tests;

// The thumbnail cache, without ffmpeg. The property that matters most here is that a cache hit does
// not run the extractor: the library is a grid of cards, so a render asks for every visible
// thumbnail at once and a cache that missed would spawn one decoder per card per render.
public sealed class ThumbnailCacheTests : IDisposable
{
    private readonly string _root;

    public ThumbnailCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tript-app-tests", nameof(ThumbnailCacheTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [SkippableFact]
    public void Ensure_RunsTheExtractorOnce_AndServesTheCachedFileAfterwards()
    {
        var video = WriteVideo("session-1.mp4");
        var extractor = new CountingExtractor();
        var store = new ThumbnailStore(ThumbnailRoot, () => extractor);

        var first = store.Ensure(video);
        var second = store.Ensure(video);
        var third = store.Ensure(video);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal(first, third);
        Assert.Equal(1, extractor.Calls);

        // The image lands in the metadata tree, keyed by the video's file name — never next to the
        // video, which keeps the sessions directory plain MP4s.
        Assert.Equal(Path.Combine(_root, "metadata", "thumbnails", "session-1.mp4.jpg"), first);
        Assert.True(File.Exists(first));
        Assert.False(File.Exists(Path.Combine(_root, "sessions", "session-1.mp4.jpg")));
    }

    [SkippableFact]
    public void Ensure_ReturnsNull_WhenTheExtractorCannotProduceAnImage()
    {
        var video = WriteVideo("session-1.mp4");
        var extractor = new CountingExtractor { Succeed = false };
        var store = new ThumbnailStore(ThumbnailRoot, () => extractor);

        Assert.Null(store.Ensure(video));
        // No empty file is left behind for a later request to serve as a thumbnail.
        Assert.Empty(Directory.Exists(ThumbnailRoot)
            ? Directory.GetFiles(ThumbnailRoot)
            : []);
    }

    // A machine with no ffmpeg is a supported state: the extractor factory returns null, every
    // request answers "no thumbnail", and the factory is not retried per request.
    [SkippableFact]
    public void Ensure_ReturnsNull_AndAsksOnce_WhenThereIsNoExtractor()
    {
        var video = WriteVideo("session-1.mp4");
        var factoryCalls = 0;
        var store = new ThumbnailStore(ThumbnailRoot, () =>
        {
            factoryCalls++;
            return null;
        });

        Assert.Null(store.Ensure(video));
        Assert.Null(store.Ensure(video));
        Assert.Equal(1, factoryCalls);
    }

    // A video replaced in place under the same name (a re-record, a restored backup) must not keep
    // serving the previous file's frame.
    [SkippableFact]
    public void Ensure_RegeneratesWhenTheVideoIsNewerThanTheCachedImage()
    {
        var video = WriteVideo("session-1.mp4");
        var extractor = new CountingExtractor();
        var store = new ThumbnailStore(ThumbnailRoot, () => extractor);

        var cached = store.Ensure(video);
        Assert.NotNull(cached);
        Assert.Equal(1, extractor.Calls);

        File.SetLastWriteTimeUtc(video, File.GetLastWriteTimeUtc(cached) + TimeSpan.FromSeconds(5));

        Assert.Equal(cached, store.Ensure(video));
        Assert.Equal(2, extractor.Calls);
    }

    [SkippableFact]
    public void Delete_RemovesTheCachedImage()
    {
        var video = WriteVideo("session-1.mp4");
        var store = new ThumbnailStore(ThumbnailRoot, () => new CountingExtractor());

        var cached = store.Ensure(video);
        Assert.NotNull(cached);
        Assert.True(File.Exists(cached));

        Assert.True(store.Delete("session-1.mp4"));
        Assert.False(File.Exists(cached));

        // Deleting a video with no cached thumbnail is not a failure.
        Assert.True(store.Delete("never-seen.mp4"));
    }

    // An unwritable cache directory must not throw at the caller (the content server's worker
    // thread): it is one more reason there is no thumbnail.
    [SkippableFact]
    public void Ensure_ReturnsNull_WhenTheCacheDirectoryCannotBeCreated()
    {
        var video = WriteVideo("session-1.mp4");
        // A regular file where the cache directory would go: Directory.CreateDirectory then fails
        // with IOException, deterministically on both platforms.
        var inTheWay = Path.Combine(_root, "blocked");
        File.WriteAllText(inTheWay, "in the way");

        var store = new ThumbnailStore(Path.Combine(inTheWay, "thumbnails"), () => new CountingExtractor());

        Assert.Null(store.Ensure(video));
    }

    private string ThumbnailRoot => Path.Combine(_root, "metadata", "thumbnails");

    private string WriteVideo(string name)
    {
        var path = Path.Combine(_root, "sessions", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not really a video; the extractor is a fake here");
        return path;
    }

    private sealed class CountingExtractor : IThumbnailExtractor
    {
        internal int Calls { get; private set; }

        internal bool Succeed { get; init; } = true;

        public bool TryExtract(string sourcePath, string destinationPath)
        {
            Calls++;
            if (!Succeed)
                return false;

            // A real extractor writes a JPEG; the bytes are irrelevant to the cache, the length is
            // not (a zero-length file counts as no thumbnail).
            File.WriteAllBytes(destinationPath, [0xFF, 0xD8, 0xFF, 0xD9]);
            return true;
        }
    }
}

// The /api/thumbnail route against a running host, with a real ffmpeg-generated source. This is
// where the status contract lives: 200 with a JPEG, 204 for every "no image" case so a grid of cards
// never produces a console full of errors, and 403 for a path that escapes the recording root.
[Collection(AppHostCollection.Name)]
public sealed class ThumbnailRouteTests : IDisposable
{
    private readonly AppHostCollectionFixture _fixture;
    private readonly string _contentRoot;
    private readonly string _settingsPath;
    private static readonly string Base = $"http://localhost:{LocalPorts.Content}";

    public ThumbnailRouteTests(AppHostCollectionFixture fixture)
    {
        _fixture = fixture;
        _contentRoot = fixture.NewContentRoot(nameof(ThumbnailRouteTests));
        _settingsPath = fixture.NewSettingsPath(nameof(ThumbnailRouteTests));
    }

    public void Dispose()
    {
    }

    [SkippableFact]
    public async Task Thumbnail_route_serves_a_jpeg_and_does_not_regenerate_it()
    {
        if (!TryLocateFfmpeg(out var ffmpeg, out var reason))
            throw new Xunit.SkipException(reason);

        var source = Path.Combine(_contentRoot, "sessions", "source.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        GenerateTestVideo(ffmpeg, source);

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;

        using var first = await GetAsync(host, "sessions/source.mp4");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("image/jpeg", first.Content.Headers.ContentType?.MediaType);
        Assert.Contains("max-age", string.Join(' ', first.Headers.GetValues("Cache-Control")));

        var bytes = await first.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 0, "the thumbnail response carried no bytes");
        // The JPEG SOI marker: the response is really an image, not a stray text body.
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]);

        // Cached in the metadata tree, keyed by the video's file name.
        var cached = Path.Combine(_contentRoot, "metadata", "thumbnails", "source.mp4.jpg");
        Assert.True(File.Exists(cached), "the thumbnail was not cached under metadata/thumbnails");
        var written = File.GetLastWriteTimeUtc(cached);

        // A second request is served from the cache: ffmpeg does not run again, so the cached file is
        // not rewritten and the bytes are identical.
        using var second = await GetAsync(host, "sessions/source.mp4");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(bytes, await second.Content.ReadAsByteArrayAsync());
        Assert.Equal(written, File.GetLastWriteTimeUtc(cached));

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public async Task Thumbnail_route_answers_204_for_a_source_it_cannot_decode_or_find()
    {
        // A text file with an .mp4 name: ffmpeg fails on it. The route must report "no thumbnail",
        // never a 500 — and the host must stay up to answer the next request.
        var corrupt = Path.Combine(_contentRoot, "sessions", "corrupt.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(corrupt)!);
        await File.WriteAllTextAsync(corrupt, "this is not an mp4");

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;

        using (var response = await GetAsync(host, "sessions/corrupt.mp4"))
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // A source that is not there at all is the same answer: the grid draws a placeholder.
        using (var missing = await GetAsync(host, "sessions/nope.mp4"))
            Assert.Equal(HttpStatusCode.NoContent, missing.StatusCode);

        // The host is still serving after both.
        using (var again = await GetAsync(host, "sessions/corrupt.mp4"))
            Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public async Task Thumbnail_route_refuses_a_path_that_escapes_the_recording_root()
    {
        var outside = Path.Combine(Path.GetTempPath(), "tript-app-tests", "sentinel", "secret.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        await File.WriteAllTextAsync(outside, "TOP SECRET");

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;

        // Raw sockets: HttpClient normalizes ".." before the request leaves, which would never reach
        // the guard.
        using (var raw = await SendRawAsync(host, "/api/thumbnail/../sentinel/secret.mp4"))
            Assert.Equal(HttpStatusCode.Forbidden, raw.StatusCode);

        using (var encoded = await SendRawAsync(host, "/api/thumbnail/%2e%2e/sentinel/secret.mp4"))
            Assert.Equal(HttpStatusCode.Forbidden, encoded.StatusCode);

        using (var mid = await SendRawAsync(host, "/api/thumbnail/sessions/../../../sentinel/secret.mp4"))
            Assert.Equal(HttpStatusCode.Forbidden, mid.StatusCode);

        // The guard itself, at its choke point: the thumbnail route resolves through the same
        // ResolveWithinRoot every content request does.
        Assert.Null(ContentServer.ResolveWithinRoot(_contentRoot, "../sentinel/secret.mp4"));
        Assert.Null(ContentServer.ResolveWithinRoot(_contentRoot, outside));

        Assert.Equal("TOP SECRET", await File.ReadAllTextAsync(outside));

        await host.ShutdownAsync();
    }

    // The cascade-delete contract extended to the cache: a deleted recording must not leave its
    // thumbnail behind, or the image would outlive the video it was taken from (and a new recording
    // reusing the name would inherit it).
    [SkippableFact]
    public async Task DeleteContent_removes_the_cached_thumbnail()
    {
        if (!TryLocateFfmpeg(out var ffmpeg, out var reason))
            throw new Xunit.SkipException(reason);

        var source = Path.Combine(_contentRoot, "sessions", "source.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        GenerateTestVideo(ffmpeg, source);

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        using (var response = await GetAsync(host, "sessions/source.mp4"))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var cached = Path.Combine(_contentRoot, "metadata", "thumbnails", "source.mp4.jpg");
        Assert.True(File.Exists(cached));

        await host.SendAsync(
            """{"method":"DeleteContent","parameters":{"fileName":"sessions/source.mp4","contentType":"recording"}}""");
        var (method, _) = await host.ReceiveAsyncParsed();
        Assert.Equal("content", method);

        Assert.False(File.Exists(source), "the video must be deleted");
        Assert.False(File.Exists(cached), "the cached thumbnail must be deleted with its video");

        await host.ShutdownAsync();
    }

    // With the launch's session token; the content server serves no thumbnail without it.
    private static Task<HttpResponseMessage> GetAsync(AppHostDriver host, string path)
        => SendAsync(new HttpRequestMessage(HttpMethod.Get, host.WithToken($"{Base}/api/thumbnail/{path}")));

    private static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        using var client = new HttpClient();
        return await client.SendAsync(request);
    }

    // Sends a raw HTTP/1.1 GET with the literal request path, bypassing HttpClient's URI
    // normalization, so the path-traversal guard is actually exercised.
    private static async Task<HttpResponseMessage> SendRawAsync(AppHostDriver host, string rawPath)
    {
        rawPath = host.WithToken(rawPath);
        using var client = new TcpClient();
        await client.ConnectAsync("localhost", LocalPorts.Content);
        await using var stream = client.GetStream();
        var request = $"GET {rawPath} HTTP/1.1\r\nHost: localhost:{LocalPorts.Content}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));

        using var buffered = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
            buffered.Write(buffer, 0, read);

        var responseText = Encoding.ASCII.GetString(buffered.ToArray());
        var statusLine = responseText.Split('\r', '\n').First();
        return new HttpResponseMessage((HttpStatusCode)int.Parse(statusLine.Split(' ')[1]));
    }

    private static bool TryLocateFfmpeg(out string ffmpeg, out string reason)
    {
        try
        {
            (ffmpeg, _) = new FfmpegLocator().Locate();
            reason = string.Empty;
            return true;
        }
        catch (FfmpegNotFoundException exception)
        {
            ffmpeg = string.Empty;
            reason = $"A real thumbnail needs ffmpeg: {exception.Message}";
            return false;
        }
    }

    // A two-second synthetic H.264 file. testsrc is a moving pattern, so the extracted frame is not
    // a flat colour and the JPEG has real content.
    private static void GenerateTestVideo(string ffmpeg, string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
                 {
                     "-y", "-loglevel", "error",
                     "-f", "lavfi", "-i", "testsrc=duration=2:size=320x240:rate=30",
                     "-c:v", "libx264", "-pix_fmt", "yuv420p",
                     path,
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("ffmpeg could not be started to build the test source.");
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !File.Exists(path))
            throw new Xunit.SkipException($"The test source could not be generated by ffmpeg: {stderr.Trim()}");
    }

    private static async Task DrainPushes(AppHostDriver host, int count)
    {
        for (var i = 0; i < count; i++)
            await host.ReceiveAsyncParsed();
    }
}
