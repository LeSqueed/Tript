// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Tript.App.Content;
using Tript.Media;
using Xunit;

namespace Tript.App.Tests;

[Collection(AppHostCollection.Name)]
public sealed class ContentServerLoadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tript-app-tests",
        nameof(ContentServerLoadTests), Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Video_range_is_not_queued_behind_a_browser_sized_thumbnail_burst()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sessions"));
        for (var index = 0; index < 6; index++)
            File.WriteAllText(Path.Combine(_root, "sessions", $"thumbnail-{index}.mp4"), $"source-{index}");
        await File.WriteAllTextAsync(Path.Combine(_root, "sessions", "playback.mp4"), "0123456789abcdefghij");

        var extractor = new BlockingExtractor();
        using var thumbnails = new ThumbnailStore(Path.Combine(_root, "metadata", "thumbnails"), () => extractor);
        var token = new SessionToken();
        var port = TestPorts.Content;
        using var server = new ContentServer(_root, token, thumbnails, port);
        server.Start();

        using var handler = new SocketsHttpHandler { MaxConnectionsPerServer = 6 };
        using var client = new HttpClient(handler);
        var thumbnailRequests = Enumerable.Range(0, 6)
            .Select(index => client.GetAsync(
                $"http://localhost:{port}/api/thumbnail/sessions/thumbnail-{index}.mp4?k={token.Value}",
                HttpCompletionOption.ResponseHeadersRead))
            .ToArray();

        try
        {
            await extractor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var thumbnailResponses = await Task.WhenAll(thumbnailRequests).WaitAsync(TimeSpan.FromSeconds(2));
            foreach (var thumbnailResponse in thumbnailResponses)
            {
                Assert.Equal(HttpStatusCode.NoContent, thumbnailResponse.StatusCode);
                thumbnailResponse.Dispose();
            }

            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"http://localhost:{port}/api/content/sessions/playback.mp4?k={token.Value}");
            request.Headers.Range = new RangeHeaderValue(2, 6);

            var stopwatch = Stopwatch.StartNew();
            using var response = await client.SendAsync(request).WaitAsync(TimeSpan.FromSeconds(2));
            stopwatch.Stop();

            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal("23456", await response.Content.ReadAsStringAsync());
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1),
                $"playback waited {stopwatch.Elapsed} behind thumbnail work");
            Assert.Equal(1, extractor.Calls);
        }
        finally
        {
            extractor.Release.Set();
        }
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

    private sealed class BlockingExtractor : IThumbnailExtractor
    {
        internal TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new(false);
        internal int Calls => Volatile.Read(ref _calls);
        private int _calls;

        public bool TryExtract(string sourcePath, string destinationPath)
        {
            Interlocked.Increment(ref _calls);
            Entered.TrySetResult(true);
            Release.Wait(TimeSpan.FromSeconds(10));
            File.WriteAllBytes(destinationPath, [0xFF, 0xD8, 0xFF, 0xD9]);
            return true;
        }
    }
}
