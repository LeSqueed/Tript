// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using Tript.Media;
using Xunit;
using Xunit.Sdk;

namespace Tript.App.Tests;

// The clip source path. The wire's filePath is relative to the effective recording root by design:
// AppHost.ListContent builds ContentItem.FilePath with Path.GetRelativePath against EffectiveRoot
// and '/' separators, because that is the form the content server's URLs take, and the frontend
// echoes that string straight back in CreateClip.
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
        // The premise of the whole test: the root is not the process CWD, so a relative source path
        // resolves to a different (nonexistent) file if it is left relative.
        Assert.NotEqual(Path.GetFullPath(Environment.CurrentDirectory), Path.GetFullPath(_root));

        var request = AppController.BuildClipRequest(Parameters("sessions/session-20260817-152046741.mp4"), _root);

        Assert.NotNull(request);
        Assert.True(Path.IsPathRooted(request.SourcePath),
            $"The clip source must be absolute, was '{request.SourcePath}'.");
        // Path.Combine here is the cross-platform assertion: the wire's '/' separators must come out
        // as the platform's, so this also pins the Windows behaviour ('\' in the resolved path).
        Assert.Equal(Path.Combine(_root, "sessions", "session-20260817-152046741.mp4"), request.SourcePath);

        // The bug, stated directly: the source must not resolve against the CWD. Before the fix
        // SourcePath was the bare wire string, which is exactly what ffmpeg resolved this way.
        Assert.NotEqual(Path.GetFullPath("sessions/session-20260817-152046741.mp4"), request.SourcePath);

        // The output path was already rooted at the recording root; it stays that way.
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
        // Refused, not resolved-and-then-checked: BuildClipRequest returns null and the command
        // never reaches the clip engine (AppController.CreateClip broadcasts an importProgress
        // error instead). This is the security property, not just a correctness one.
        Assert.Null(AppController.BuildClipRequest(Parameters(filePath), _root));
    }

    [SkippableFact]
    public void An_absolute_source_path_outside_the_recording_root_is_refused()
    {
        var outside = Path.Combine(Path.GetTempPath(), "tript-app-tests", "sentinel", "secret.mp4");
        Assert.Null(AppController.BuildClipRequest(Parameters(outside), _root));

        // The classic prefix trap: a sibling directory whose name starts with the root's must not
        // pass for a path inside the root.
        var sibling = Path.Combine(_root + "-other", "sessions", "secret.mp4");
        Assert.Null(AppController.BuildClipRequest(Parameters(sibling), _root));
    }

    [SkippableFact]
    public void An_absolute_source_path_inside_the_recording_root_is_accepted()
    {
        // The wire sends a relative path today, but an absolute one that genuinely points inside the
        // root is a legitimate source and is passed through as-is.
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
        // The engine names the per-region files from the source path, which is now absolute; the
        // names it derives are unchanged because it uses the file name only.
        Assert.Equal(Path.Combine(_root, "sessions", "session-x.mp4"), request.SourcePath);
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

// The same bug at the seam the user hit: a running app host, a session file under the recording
// root, and a process CWD that is not that root. The host is a child process started from the test
// runner's directory, so the CWD is wrong for a relative source path by construction — which is
// precisely the condition the old code needed to fail, and the reason a real clip round trip is
// worth its ffmpeg dependency here.
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

        // The host inherits this process's working directory (the test runner's output directory),
        // never the content root — assert it, because the whole test rests on it.
        Assert.NotEqual(Path.GetFullPath(Environment.CurrentDirectory), Path.GetFullPath(_contentRoot));

        // A real, probeable MP4: MediaProbe shells out to ffprobe, so a text file with an .mp4 name
        // cannot distinguish "resolved to the wrong directory" from "not a video".
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
        // Before the fix this was status=error with "Source file does not exist:
        // <test-runner-directory>/sessions/source.mp4" — the file existed, under the recording root.
        Assert.Equal("done", status);
        Assert.Null(error);

        // The clip landed under the recording root, and the wire path is relative to it again.
        var filePath = content.GetProperty("content").GetProperty("filePath").GetString();
        Assert.Equal("clips/source-clip-cwd-1.mp4", filePath);
        Assert.True(File.Exists(Path.Combine(_contentRoot, "clips", "source-clip-cwd-1.mp4")),
            "The clip file was not written under the recording root.");

        await host.ShutdownAsync();
    }

    [SkippableFact]
    public async Task CreateClip_refuses_a_source_path_that_escapes_the_recording_root()
    {
        // A sentinel outside the root that a successful traversal would have read.
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

        // The refusal is reported, not swallowed: one importProgress error frame, the same shape the
        // engine's own failures use, so the clip dialog shows a failure the user can act on.
        var (method, content) = await host.ReceiveAsyncParsed();
        Assert.Equal("importProgress", method);
        Assert.Equal("error", content.GetProperty("status").GetString());
        Assert.Contains("not inside the recording folder", content.GetProperty("error").GetString());

        // Nothing was clipped, and the sentinel is untouched.
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

    // A two-second synthetic H.264 file. Video only: the engine maps as many audio tracks as the
    // source has, so zero is a valid (and faster) source.
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
    // The clip id arrives raw off the socket and is concatenated into a file name. Before this it
    // could carry separators and walk out of the recording folder, with the engine creating the
    // directories on the way.
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

    // A hostile id must not be able to steer the composed output path out of the clips directory.
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
