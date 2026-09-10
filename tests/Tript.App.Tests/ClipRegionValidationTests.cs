// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using Tript.Media;
using Xunit;
using Xunit.Sdk;

namespace Tript.App.Tests;

public sealed class ClipRegionParsingTests
{
    private readonly string _root;

    public ClipRegionParsingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tript-app-tests", nameof(ClipRegionParsingTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [SkippableTheory]
    [InlineData(0.0, 1e18)]
    [InlineData(0.0, 1e308)]
    [InlineData(-1e308, 1.0)]
    public void Unrepresentable_segment_times_are_refused_rather_than_thrown(double start, double end)
    {
        var request = AppController.BuildClipRequest(
            Parameters("sessions/session-x.mp4", (start, end)), _root, out var refusal);

        Assert.Null(request);
        Assert.NotNull(refusal);
        Assert.Contains("not real times", refusal);
    }

    [SkippableFact]
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

    [SkippableFact]
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

    [SkippableFact]
    public void Out_of_bounds_but_representable_times_survive_this_layer()
    {
        var request = AppController.BuildClipRequest(
            Parameters("sessions/session-x.mp4", (-30.0, 3600.0)), _root, out var refusal);

        Assert.Null(refusal);
        Assert.NotNull(request);
        Assert.Equal(TimeSpan.FromSeconds(-30.0), Assert.Single(request.Regions).Start);
    }

    [SkippableFact]
    public void A_traversal_is_still_reported_as_a_traversal_even_with_nonsense_times()
    {
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

    [SkippableFact]
    public async Task CreateClip_with_every_region_past_the_end_reports_an_error_and_writes_nothing()
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

        var clipsDirectory = Path.Combine(_contentRoot, "clips");
        Assert.True(!Directory.Exists(clipsDirectory) || Directory.GetFiles(clipsDirectory).Length == 0,
            "a refused clip must not leave a file behind");

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public async Task CreateClip_with_an_unrepresentable_time_reports_an_error_and_leaves_the_host_running()
    {
        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""
            {"method":"CreateClip","parameters":{
              "id":"clip-overflow-1",
              "filePath":"sessions/source.mp4",
              "outputMode":"combine",
              "startTime":0,"endTime":1e18,
              "segments":[]
            }}
            """);

        var (method, content) = await host.ReceiveAsyncParsed();
        Assert.Equal("importProgress", method);
        Assert.Equal("error", content.GetProperty("status").GetString());
        Assert.Contains("not real times", content.GetProperty("error").GetString());

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
