// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using Tript.Media;
using Xunit;
using Xunit.Sdk;

namespace Tript.App.Tests;

public sealed class ClipPathResolutionTests : IDisposable
{
    private readonly string _root;

    public ClipPathResolutionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tript-app-tests", nameof(ClipPathResolutionTests),
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
    public void Wire_relative_source_path_resolves_against_the_effective_root()
    {
        Assert.NotEqual(Path.GetFullPath(Environment.CurrentDirectory), Path.GetFullPath(_root));

        var request = AppController.BuildClipRequest(Parameters("sessions/session-20260817-152046741.mp4"), _root);

        Assert.NotNull(request);
        Assert.True(Path.IsPathRooted(request.SourcePath),
            $"The clip source must be absolute, was '{request.SourcePath}'.");

        Assert.Equal(Path.Combine(_root, "sessions", "session-20260817-152046741.mp4"), request.SourcePath);
        Assert.Equal("sessions/session-20260817-152046741.mp4", request.SourceSessionPath);

        Assert.NotEqual(Path.GetFullPath("sessions/session-20260817-152046741.mp4"), request.SourcePath);

        Assert.Equal(Path.Combine(_root, "clips", "session-20260817-152046741-clip-abc.mp4"), request.OutputPath);
    }

    [SkippableTheory]
    [InlineData("../../secret.mp4")]
    [InlineData("../secret.mp4")]
    [InlineData("sessions/../../secret.mp4")]
    [InlineData("..")]
    [InlineData("")]
    public void A_source_path_that_escapes_the_recording_root_is_refused(string filePath)
    {
        Assert.Null(AppController.BuildClipRequest(Parameters(filePath), _root));
    }

    [SkippableFact]
    public void An_absolute_source_path_outside_the_recording_root_is_refused()
    {
        var outside = Path.Combine(Path.GetTempPath(), "tript-app-tests", "sentinel", "secret.mp4");
        Assert.Null(AppController.BuildClipRequest(Parameters(outside), _root));

        var sibling = Path.Combine(_root + "-other", "sessions", "secret.mp4");
        Assert.Null(AppController.BuildClipRequest(Parameters(sibling), _root));
    }

    [SkippableFact]
    public void An_absolute_source_path_inside_the_recording_root_is_accepted()
    {
        var inside = Path.Combine(_root, "sessions", "session-x.mp4");

        var request = AppController.BuildClipRequest(Parameters(inside), _root);

        Assert.NotNull(request);
        Assert.Equal(inside, request.SourcePath);
    }

    [SkippableFact]
    public void Separate_mode_keeps_the_clips_directory_as_its_output()
    {
        var request = AppController.BuildClipRequest(
            Parameters("sessions/session-x.mp4", outputMode: "separate"), _root);

        Assert.NotNull(request);
        Assert.Equal(ClipMode.Separate, request.Mode);
        Assert.Equal(Path.Combine(_root, "clips"), request.OutputPath);

        Assert.Equal(Path.Combine(_root, "sessions", "session-x.mp4"), request.SourcePath);
    }

    [SkippableFact]
    public void Game_scoped_source_writes_clips_beside_its_sessions_directory()
    {
        var request = AppController.BuildClipRequest(
            Parameters("Overwatch/sessions/session-x.mp4"), _root);

        Assert.NotNull(request);
        Assert.Equal(Path.Combine(_root, "Overwatch", "clips", "session-x-clip-abc.mp4"), request.OutputPath);
        Assert.Equal("Overwatch/sessions/session-x.mp4", request.SourceSessionPath);
    }

    private static CreateClipParameters Parameters(string filePath, string outputMode = "combine") => new()
    {
        Id = "clip-abc",
        FilePath = filePath,
        OutputMode = outputMode,
        StartTime = 0,
        EndTime = 1,
    };
}

[Collection(AppHostCollection.Name)]
public sealed class ClipSourceResolutionSmokeTests : IDisposable
{
    private readonly AppHostCollectionFixture _fixture;
    private readonly string _contentRoot;
    private readonly string _settingsPath;

    public ClipSourceResolutionSmokeTests(AppHostCollectionFixture fixture)
    {
        _fixture = fixture;
        _contentRoot = fixture.NewContentRoot(nameof(ClipSourceResolutionSmokeTests));
        _settingsPath = fixture.NewSettingsPath(nameof(ClipSourceResolutionSmokeTests));
    }

    public void Dispose()
    {
    }

    [SkippableFact]
    public async Task CreateClip_clips_a_session_under_the_recording_root_from_a_foreign_cwd()
    {
        if (!TryLocateFfmpeg(out var ffmpeg, out var reason))
            throw new Xunit.SkipException(reason);

        Assert.NotEqual(Path.GetFullPath(Environment.CurrentDirectory), Path.GetFullPath(_contentRoot));

        var source = Path.Combine(_contentRoot, "sessions", "source.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        GenerateTestVideo(ffmpeg, source);

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""
            {"method":"CreateClip","parameters":{
              "id":"clip-cwd-1",
              "filePath":"sessions/source.mp4",
              "outputMode":"combine",
              "startTime":0,"endTime":1,
              "segments":[]
            }}
            """);

        var (importing, _) = await host.ReceiveAsyncParsed();
        Assert.Equal("importProgress", importing);

        var (result, content) = await host.ReceiveAsyncParsed();
        Assert.Equal("importProgress", result);
        var status = content.GetProperty("status").GetString();
        var error = content.TryGetProperty("error", out var errorElement) ? errorElement.GetString() : null;

        Assert.Equal("done", status);
        Assert.Null(error);

        var filePath = content.GetProperty("content").GetProperty("filePath").GetString();
        Assert.Equal("clips/source-clip-cwd-1.mp4", filePath);
        Assert.True(File.Exists(Path.Combine(_contentRoot, "clips", "source-clip-cwd-1.mp4")),
            "The clip file was not written under the recording root.");

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public async Task CreateClip_refuses_a_source_path_that_escapes_the_recording_root()
    {
        var outside = Path.Combine(Path.GetTempPath(), "tript-app-tests", "sentinel", "secret.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        await File.WriteAllTextAsync(outside, "TOP SECRET");

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;
        await host.ConnectWebSocketAsync();
        await DrainPushes(host, 3);

        await host.SendAsync("""
            {"method":"CreateClip","parameters":{
              "id":"clip-traversal-1",
              "filePath":"../sentinel/secret.mp4",
              "outputMode":"combine",
              "startTime":0,"endTime":1,
              "segments":[]
            }}
            """);

        var (method, content) = await host.ReceiveAsyncParsed();
        Assert.Equal("importProgress", method);
        Assert.Equal("error", content.GetProperty("status").GetString());
        Assert.Contains("not inside the recording folder", content.GetProperty("error").GetString());

        Assert.False(Directory.Exists(Path.Combine(_contentRoot, "clips")));
        Assert.Equal("TOP SECRET", await File.ReadAllTextAsync(outside));

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

    [SkippableTheory]
    [InlineData("x/../../../../tmp/pwn")]
    [InlineData("../../etc/cron.d/x")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    public void SafeClipId_KeepsAnIdToASingleNameSegment(string hostile)
    {
        var safe = AppController.SafeClipId(hostile);

        Assert.DoesNotContain('/', safe);
        Assert.DoesNotContain('\\', safe);
        Assert.DoesNotContain("..", safe, StringComparison.Ordinal);
        Assert.NotEmpty(safe);
    }

    [SkippableFact]
    public void SafeClipId_KeepsAnOrdinaryIdAsItIs()
    {
        Assert.Equal("clip-42_A", AppController.SafeClipId("clip-42_A"));
    }

    [SkippableFact]
    public void SafeClipId_GivesAnIdThatSurvivesNothingAGeneratedOne()
    {
        var safe = AppController.SafeClipId("../..");

        Assert.NotEmpty(safe);
        Assert.DoesNotContain('.', safe);
    }

    [SkippableFact]
    public void SafeClipId_IsBounded()
    {
        Assert.True(AppController.SafeClipId(new string('a', 5000)).Length <= 64);
    }

    [SkippableFact]
    public void BuildClipOutputPath_StaysUnderTheClipsDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "tript-clip-output", Guid.NewGuid().ToString("N"));
        var parameters = new CreateClipParameters
        {
            FilePath = "sessions/session-1.mp4",
            Id = "x/../../../../../../tmp/pwn",
            OutputMode = "combine",
        };

        var output = AppController.BuildClipOutputPath(parameters, root);

        var clips = Path.GetFullPath(Path.Combine(root, "clips"));
        Assert.StartsWith(clips + Path.DirectorySeparatorChar, Path.GetFullPath(output), StringComparison.Ordinal);
    }
}
