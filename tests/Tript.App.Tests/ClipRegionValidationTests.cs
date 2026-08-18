// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using Tript.Media;
using Xunit;
using Xunit.Sdk;

namespace Tript.App.Tests;

// The backend's gate on clip region bounds, at the seam it actually defends: the control socket.
// The frontend clamps the timeline selection to the media duration, but that is a UX affordance —
// it keeps the handles inside the scrubber — and the socket is a trust boundary regardless of being
// local.
public sealed class ClipRegionParsingTests
{
    private readonly string _root;

    public ClipRegionParsingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tript-app-tests", nameof(ClipRegionParsingTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Theory]
    [InlineData(0.0, 1e18)]
    [InlineData(0.0, 1e308)]
    [InlineData(-1e308, 1.0)]
    public void Unrepresentable_segment_times_are_refused_rather_than_thrown(double start, double end)
    {
        // The pre-fix behaviour was a throw, not a refusal: BuildClipRequest mapped the wire seconds
        // straight through ClipRegion.FromSeconds, and TimeSpan.FromSeconds throws OverflowException
        // past ~9.22e11 seconds — 1e18 is an unremarkable JSON number that reaches it. This runs on the
        // IPC dispatch thread, where IpcServer.Dispatch catches everything and only writes it to
        // stderr, so the frontend received no frame at all: no "importing", no error, nothing for the
        // clip dialog to render.
        var request = AppController.BuildClipRequest(
            Parameters("sessions/session-x.mp4", (start, end)), _root, out var refusal);

        Assert.Null(request);
        Assert.NotNull(refusal);
        Assert.Contains("not real times", refusal);
    }

    [Fact]
    public void A_swapped_segment_is_ordered_rather_than_refused()
    {
        var request = AppController.BuildClipRequest(
            Parameters("sessions/session-x.mp4", (9.5, 1.25)), _root, out var refusal);

        Assert.Null(refusal);
        Assert.NotNull(request);
        var region = Assert.Single(request.Regions);
        Assert.Equal(TimeSpan.FromSeconds(1.25), region.Start);
        Assert.Equal(TimeSpan.FromSeconds(9.5), region.End);
    }

    [Fact]
    public void One_unusable_segment_does_not_discard_the_usable_ones()
    {
        var request = AppController.BuildClipRequest(
            Parameters("sessions/session-x.mp4", (0.0, 1.0), (0.0, 1e18), (2.0, 3.0)), _root, out var refusal);

        Assert.Null(refusal);
        Assert.NotNull(request);
        Assert.Equal(2, request.Regions.Count);
        Assert.Equal(TimeSpan.Zero, request.Regions[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(2.0), request.Regions[1].Start);
    }

    [Fact]
    public void Out_of_bounds_but_representable_times_survive_this_layer()
    {
        // Deliberately: the clamp to [0, duration] needs the source's real length, which is not known
        // until MediaProbe has run inside the engine. This layer only refuses times that are not times.
        var request = AppController.BuildClipRequest(
            Parameters("sessions/session-x.mp4", (-30.0, 3600.0)), _root, out var refusal);

        Assert.Null(refusal);
        Assert.NotNull(request);
        Assert.Equal(TimeSpan.FromSeconds(-30.0), Assert.Single(request.Regions).Start);
    }

    [Fact]
    public void A_traversal_is_still_reported_as_a_traversal_even_with_nonsense_times()
    {
        // Ordering of the two refusals matters: the security-relevant message must not be masked by a
        // complaint about timestamps.
        var request = AppController.BuildClipRequest(
            Parameters("../secret.mp4", (double.NaN, 1e18)), _root, out var refusal);

        Assert.Null(request);
        Assert.NotNull(refusal);
        Assert.Contains("not inside the recording folder", refusal);
    }

    private static CreateClipParameters Parameters(string filePath, params (double Start, double End)[] segments) =>
        new()
        {
            Id = "clip-abc",
            FilePath = filePath,
            OutputMode = "combine",
            Segments = segments.Select(s => new ClipSegment { StartTime = s.Start, EndTime = s.End }).ToList(),
        };
}

// The same gate through a running host, so the failure channel is exercised and not just the parsing.
// Both cases below produced nothing at all for the frontend before the fix, in different ways: an
// unrepresentable time threw on the dispatch thread (stderr only), and an out-of-bounds region reached
// ffmpeg, which reports exit code 0 and an empty output file for it.
[Collection(AppHostCollection.Name)]
public sealed class ClipRegionBoundsSmokeTests
{
    private readonly string _contentRoot;
    private readonly string _settingsPath;

    public ClipRegionBoundsSmokeTests(AppHostCollectionFixture fixture)
    {
        _contentRoot = fixture.NewContentRoot(nameof(ClipRegionBoundsSmokeTests));
        _settingsPath = fixture.NewSettingsPath(nameof(ClipRegionBoundsSmokeTests));
    }

    [Fact]
    public async Task CreateClip_with_every_region_past_the_end_reports_an_error_and_writes_nothing()
    {
        if (!TryLocateFfmpeg(out var ffmpeg, out var reason))
            throw SkipException.ForSkip(reason);

        // A real 2 s H.264 file, because the bound is the source's probed duration — a text file with
        // an .mp4 name would fail at MediaProbe instead and prove nothing about the clamp.
        var source = Path.Combine(_contentRoot, "sessions", "source.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        GenerateTestVideo(ffmpeg, source);

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        // 60s - 120s of a 2s recording: the shape the frontend's 120 s placeholder duration produced.
        await host.SendAsync("""
            {"method":"CreateClip","parameters":{
              "id":"clip-beyond-1",
              "filePath":"sessions/source.mp4",
              "outputMode":"combine",
              "startTime":0,"endTime":0,
              "segments":[{"startTime":60,"endTime":120}]
            }}
            """);

        var (importing, _) = await host.ReceiveAsyncParsed();
        Assert.Equal("importProgress", importing);

        var (method, content) = await host.ReceiveAsyncParsed();
        Assert.Equal("importProgress", method);
        Assert.Equal("error", content.GetProperty("status").GetString());
        var error = content.GetProperty("error").GetString();
        Assert.Contains("nothing to clip", error);

        // Nothing was produced. Unclamped, ffmpeg would have exited 0 here and written a 261-byte MP4
        // with no video stream (measured), and the host would have reported status=done for it.
        var clipsDirectory = Path.Combine(_contentRoot, "clips");
        Assert.True(!Directory.Exists(clipsDirectory) || Directory.GetFiles(clipsDirectory).Length == 0,
            "a refused clip must not leave a file behind");

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task CreateClip_with_an_unrepresentable_time_reports_an_error_and_leaves_the_host_running()
    {
        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        // 1e18 is a valid JSON number and an invalid TimeSpan. No ffmpeg is needed: the refusal happens
        // before the source is ever looked at.
        await host.SendAsync("""
            {"method":"CreateClip","parameters":{
              "id":"clip-overflow-1",
              "filePath":"sessions/source.mp4",
              "outputMode":"combine",
              "startTime":0,"endTime":1e18,
              "segments":[]
            }}
            """);

        // The error is the FIRST frame, not the second: the clip never started, so there is no
        // "importing" ahead of it. Before the fix there was no frame at all — the OverflowException was
        // swallowed by IpcServer.Dispatch into a stderr line and the clip dialog waited forever.
        var (method, content) = await host.ReceiveAsyncParsed();
        Assert.Equal("importProgress", method);
        Assert.Equal("error", content.GetProperty("status").GetString());
        Assert.Contains("not real times", content.GetProperty("error").GetString());

        // The dispatch thread survived it, which an unhandled throw on the IPC surface does not
        // guarantee: the host still answers the next command.
        await host.SendAsync("""{"method":"ListSettings"}""");
        var (settingsMethod, _) = await host.ReceiveAsyncParsed();
        Assert.Equal("settings", settingsMethod);

        await host.ShutdownAsync();
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
            reason = $"A real clip round trip needs ffmpeg: {exception.Message}";
            return false;
        }
    }

    // A two-second synthetic H.264 file, video only.
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
            throw SkipException.ForSkip($"The test source could not be generated by ffmpeg: {stderr.Trim()}");
    }

    private static async Task DrainPushes(AppHostDriver host, int count)
    {
        for (var i = 0; i < count; i++)
            await host.ReceiveAsyncParsed();
    }
}
