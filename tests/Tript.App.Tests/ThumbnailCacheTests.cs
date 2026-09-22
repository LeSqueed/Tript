// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using System.Net;
using Tript.App.Content;
using Tript.Media;
using Xunit;
using Xunit.Sdk;

namespace Tript.App.Tests;

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
    public async Task Ensure_RunsTheExtractorOnce_AndServesTheCachedFileAfterwards()
    {
        var video = WriteVideo("session-1.mp4");
        var extractor = new CountingExtractor();
        using var store = new ThumbnailStore(ThumbnailRoot, () => extractor);

        var first = await store.EnsureAsync(video);
        var second = await store.EnsureAsync(video);
        var third = await store.EnsureAsync(video);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal(first, third);
        Assert.Equal(1, extractor.Calls);

        Assert.Equal(Path.Combine(_root, "metadata", "thumbnails", "session-1.mp4.jpg"), first);
        Assert.True(File.Exists(first));
        Assert.False(File.Exists(Path.Combine(_root, "sessions", "session-1.mp4.jpg")));
    }

    [SkippableFact]
    public async Task Ensure_ReturnsNull_WhenTheExtractorCannotProduceAnImage()
    {
        var video = WriteVideo("session-1.mp4");
        var extractor = new CountingExtractor { Succeed = false };
        using var store = new ThumbnailStore(ThumbnailRoot, () => extractor);

        Assert.Null(await store.EnsureAsync(video));
        Assert.Null(store.GetOrQueue(video));
        await Task.Delay(100);
        Assert.Equal(1, extractor.Calls);

        Assert.Empty(Directory.Exists(ThumbnailRoot)
            ? Directory.GetFiles(ThumbnailRoot)
            : []);
    }

    [SkippableFact]
    public async Task Ensure_ReturnsNull_AndAsksOnce_WhenThereIsNoExtractor()
    {
        var video = WriteVideo("session-1.mp4");
        var factoryCalls = 0;
        using var store = new ThumbnailStore(ThumbnailRoot, () =>
        {
            factoryCalls++;
            return null;
        });

        Assert.Null(await store.EnsureAsync(video));
        Assert.Null(await store.EnsureAsync(video));
        Assert.Equal(1, factoryCalls);
    }

    [SkippableFact]
    public async Task Ensure_RegeneratesWhenTheVideoIsNewerThanTheCachedImage()
    {
        var video = WriteVideo("session-1.mp4");
        var extractor = new CountingExtractor();
        using var store = new ThumbnailStore(ThumbnailRoot, () => extractor);

        var cached = await store.EnsureAsync(video);
        Assert.NotNull(cached);
        Assert.Equal(1, extractor.Calls);

        File.SetLastWriteTimeUtc(video, File.GetLastWriteTimeUtc(cached) + TimeSpan.FromSeconds(5));

        Assert.Equal(cached, await store.EnsureAsync(video));
        Assert.Equal(2, extractor.Calls);
    }

    [SkippableFact]
    public async Task Delete_RemovesTheCachedImage()
    {
        var video = WriteVideo("session-1.mp4");
        using var store = new ThumbnailStore(ThumbnailRoot, () => new CountingExtractor());

        var cached = await store.EnsureAsync(video);
        Assert.NotNull(cached);
        Assert.True(File.Exists(cached));

        Assert.True(store.Delete("session-1.mp4"));
        Assert.False(File.Exists(cached));

        Assert.True(store.Delete("never-seen.mp4"));
    }

    [SkippableFact]
    public async Task Ensure_ReturnsNull_WhenTheCacheDirectoryCannotBeCreated()
    {
        var video = WriteVideo("session-1.mp4");

        var inTheWay = Path.Combine(_root, "blocked");
        File.WriteAllText(inTheWay, "in the way");

        using var store = new ThumbnailStore(Path.Combine(inTheWay, "thumbnails"), () => new CountingExtractor());

        Assert.Null(await store.EnsureAsync(video));
    }

    [Fact]
    public async Task Cache_misses_return_immediately_and_coalesce_while_extraction_is_blocked()
    {
        var video = WriteVideo("slow.mp4");
        var extractor = new BlockingExtractor();
        using var store = new ThumbnailStore(ThumbnailRoot, () => extractor);

        Assert.Null(store.GetOrQueue(video));
        Assert.True(extractor.Entered.Wait(TimeSpan.FromSeconds(2)), "the background worker did not start");

        var stopwatch = Stopwatch.StartNew();
        for (var index = 0; index < 20; index++)
            Assert.Null(store.GetOrQueue(video));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"cache misses waited on extraction for {stopwatch.Elapsed}");
        Assert.Equal(1, extractor.Calls);

        var completion = store.EnsureAsync(video);
        Assert.False(completion.IsCompleted);
        extractor.Release.Set();
        Assert.NotNull(await completion.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, extractor.Calls);
    }

    [Fact]
    public async Task Invalidate_prevents_an_in_flight_extraction_from_publishing()
    {
        var video = WriteVideo("moving-to-trash.mp4");
        var extractor = new BlockingExtractor();
        using var store = new ThumbnailStore(ThumbnailRoot, () => extractor);

        var completion = store.EnsureAsync(video);
        Assert.True(extractor.Entered.Wait(TimeSpan.FromSeconds(2)), "the background worker did not start");

        store.Invalidate(Path.GetFileName(video));
        extractor.Release.Set();

        Assert.Null(await completion.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(File.Exists(store.PathFor(Path.GetFileName(video))));
    }

    [Fact]
    public async Task HoldForRemoval_QueuesNoExtractionUntilReleased()
    {
        var video = WriteVideo("being-deleted.mp4");
        var extractor = new CountingExtractor();
        using var store = new ThumbnailStore(ThumbnailRoot, () => extractor);

        using (store.HoldForRemoval(Path.GetFileName(video), TimeSpan.FromSeconds(1)))
        {
            Assert.Null(await store.EnsureAsync(video).WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(0, extractor.Calls);
        }

        Assert.NotNull(await store.EnsureAsync(video).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, extractor.Calls);
    }

    [Fact]
    public async Task HoldForRemoval_WaitsForARunningExtractionToLetGoOfTheVideo()
    {
        var video = WriteVideo("in-use.mp4");
        var extractor = new BlockingExtractor();
        using var store = new ThumbnailStore(ThumbnailRoot, () => extractor);

        _ = store.EnsureAsync(video);
        Assert.True(extractor.Entered.Wait(TimeSpan.FromSeconds(2)), "the background worker did not start");

        var holdTask = Task.Run(() => store.HoldForRemoval(Path.GetFileName(video), TimeSpan.FromSeconds(10)));
        Assert.NotSame(holdTask, await Task.WhenAny(holdTask, Task.Delay(TimeSpan.FromMilliseconds(300))));

        extractor.Release.Set();
        (await holdTask.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task HoldForRemoval_OverlappingHoldsOnOneName_KeepBlockingUntilTheLastIsReleased()
    {
        var video = WriteVideo("twice.mp4");
        var extractor = new CountingExtractor();
        using var store = new ThumbnailStore(ThumbnailRoot, () => extractor);
        var name = Path.GetFileName(video);

        var first = store.HoldForRemoval(name, TimeSpan.Zero);
        var second = store.HoldForRemoval(name, TimeSpan.Zero);
        first.Dispose();

        Assert.Null(await store.EnsureAsync(video).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, extractor.Calls);

        second.Dispose();
        Assert.NotNull(await store.EnsureAsync(video).WaitAsync(TimeSpan.FromSeconds(2)));
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

            File.WriteAllBytes(destinationPath, [0xFF, 0xD8, 0xFF, 0xD9]);
            return true;
        }
    }

    private sealed class BlockingExtractor : IThumbnailExtractor
    {
        internal ManualResetEventSlim Entered { get; } = new(false);
        internal ManualResetEventSlim Release { get; } = new(false);
        internal int Calls => Volatile.Read(ref _calls);
        private int _calls;

        public bool TryExtract(string sourcePath, string destinationPath)
        {
            Interlocked.Increment(ref _calls);
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(10));
            File.WriteAllBytes(destinationPath, [0xFF, 0xD8, 0xFF, 0xD9]);
            return true;
        }
    }
}

[Collection(AppHostCollection.Name)]
public sealed class ThumbnailRouteTests : IDisposable
{
    private readonly AppHostCollectionFixture _fixture;
    private readonly string _contentRoot;
    private readonly string _settingsPath;

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

        using (var queued = await GetAsync(host, "sessions/source.mp4"))
            Assert.Equal(HttpStatusCode.NoContent, queued.StatusCode);

        using var first = await GetUntilReadyAsync(host, "sessions/source.mp4");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("image/jpeg", first.Content.Headers.ContentType?.MediaType);
        Assert.Contains("max-age", string.Join(' ', first.Headers.GetValues("Cache-Control")));

        var bytes = await first.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 0, "the thumbnail response carried no bytes");

        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]);

        var cached = Path.Combine(_contentRoot, "metadata", "thumbnails", "source.mp4.jpg");
        Assert.True(File.Exists(cached), "the thumbnail was not cached under metadata/thumbnails");
        var written = File.GetLastWriteTimeUtc(cached);

        using var second = await GetAsync(host, "sessions/source.mp4");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(bytes, await second.Content.ReadAsByteArrayAsync());
        Assert.Equal(written, File.GetLastWriteTimeUtc(cached));

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public async Task Thumbnail_route_answers_204_for_a_source_it_cannot_decode_or_find()
    {
        var corrupt = Path.Combine(_contentRoot, "sessions", "corrupt.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(corrupt)!);
        await File.WriteAllTextAsync(corrupt, "this is not an mp4");

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;

        using (var response = await GetAsync(host, "sessions/corrupt.mp4"))
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using (var missing = await GetAsync(host, "sessions/nope.mp4"))
            Assert.Equal(HttpStatusCode.NoContent, missing.StatusCode);

        using (var again = await GetAsync(host, "sessions/corrupt.mp4"))
            Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);

        await host.ShutdownAsync();
    }

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

        using (var response = await GetUntilReadyAsync(host, "sessions/source.mp4"))
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

    private static Task<HttpResponseMessage> GetAsync(AppHostDriver host, string path)
        => SendAsync(new HttpRequestMessage(HttpMethod.Get,
            host.WithToken($"http://localhost:{host.ContentPort}/api/thumbnail/{path}")));

    private static async Task<HttpResponseMessage> GetUntilReadyAsync(AppHostDriver host, string path)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(15))
        {
            var response = await GetAsync(host, path);
            if (response.StatusCode == HttpStatusCode.OK)
                return response;
            response.Dispose();
            await Task.Delay(100);
        }

        throw new TimeoutException($"Thumbnail '{path}' was not generated within 15 seconds.");
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        using var client = new HttpClient();
        return await client.SendAsync(request);
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
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            process.WaitForExit(TimeSpan.FromSeconds(10));
            throw new Xunit.SkipException("ffmpeg did not finish generating the test source within 60 seconds.");
        }
        stdout.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0 || !File.Exists(path))
            throw new Xunit.SkipException($"The test source could not be generated by ffmpeg: {stderr.Trim()}");
    }

    private static async Task DrainPushes(AppHostDriver host, int count)
    {
        for (var i = 0; i < count; i++)
            await host.ReceiveAsyncParsed();
    }
}
