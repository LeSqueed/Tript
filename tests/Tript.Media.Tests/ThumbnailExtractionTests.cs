// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Media.Tests;

public sealed class ThumbnailExtractionTests
{
    [Fact]
    public void TryExtract_WritesAJpegFrameFromASdrSource()
    {
        var source = MediaTestFixture.CreateSdrSource("thumbnail-sdr.mp4", durationSeconds: 4, audioTracks: 2);
        var destination = Path.Combine(MediaTestFixture.ScratchRoot, "thumbnail-sdr.jpg");
        var extractor = new FfmpegThumbnailExtractor(MediaTestFixture.Binaries.Ffmpeg);

        Assert.True(extractor.TryExtract(source, destination));

        Assert.True(new FileInfo(destination).Length > 0);
        Assert.Equal("mjpeg", MediaTestFixture.ProbeValue(MediaTestFixture.Binaries.Ffprobe, destination, "v:0", "codec_name"));

        Assert.Equal("480", MediaTestFixture.ProbeValue(MediaTestFixture.Binaries.Ffprobe, destination, "v:0", "width"));
        Assert.Equal("360", MediaTestFixture.ProbeValue(MediaTestFixture.Binaries.Ffprobe, destination, "v:0", "height"));
    }

    [Fact]
    public void TryExtract_FallsBackToTheFirstFrame_WhenTheSourceIsShorterThanTheSeek()
    {
        var source = MediaTestFixture.CreateSdrSource("thumbnail-tiny.mp4", durationSeconds: 0.5, audioTracks: 0);
        Assert.InRange(MediaTestFixture.ProbeDuration(MediaTestFixture.Binaries.Ffprobe, source), 0.1, 0.9);

        var destination = Path.Combine(MediaTestFixture.ScratchRoot, "thumbnail-tiny.jpg");
        var extractor = new FfmpegThumbnailExtractor(MediaTestFixture.Binaries.Ffmpeg);

        Assert.True(extractor.TryExtract(source, destination),
            "a recording shorter than the seek offset must still produce a thumbnail");
        Assert.True(new FileInfo(destination).Length > 0);
    }

    [Fact]
    public void TryExtract_SkipsBlackCandidates_WhenALaterFrameIsUsable()
    {
        var source = CreateBlackThenColorSource("thumbnail-black-first.mp4");
        var destination = Path.Combine(MediaTestFixture.ScratchRoot, "thumbnail-black-first.jpg");
        var extractor = new FfmpegThumbnailExtractor(MediaTestFixture.Binaries.Ffmpeg, MediaTestFixture.Binaries.Ffprobe);

        Assert.True(extractor.TryExtract(source, destination));

        var diagnostics = MediaTestFixture.Run(MediaTestFixture.Binaries.Ffmpeg,
        [
            "-hide_banner", "-i", destination, "-map", "0:v:0",
            "-vf", "blackframe=amount=98:threshold=32", "-frames:v", "1", "-an", "-f", "null", "-",
        ]);
        Assert.DoesNotContain("blackframe", diagnostics, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryExtract_UsesTheFirstFrame_WhenEveryCandidateIsBlack()
    {
        var source = Path.Combine(MediaTestFixture.ScratchRoot, "thumbnail-all-black.mp4");
        MediaTestFixture.Run(MediaTestFixture.Binaries.Ffmpeg,
        [
            "-hide_banner", "-y", "-f", "lavfi", "-i", "color=c=black:size=320x240:rate=30:duration=2",
            "-c:v", "libx264", "-g", "30", source,
        ]);
        var destination = Path.Combine(MediaTestFixture.ScratchRoot, "thumbnail-all-black.jpg");
        var extractor = new FfmpegThumbnailExtractor(MediaTestFixture.Binaries.Ffmpeg, MediaTestFixture.Binaries.Ffprobe);

        Assert.True(extractor.TryExtract(source, destination));
        Assert.True(new FileInfo(destination).Length > 0);
    }

    [Fact]
    public void TryExtract_RunsFfmpegOnce_WhenTheChosenSeekProducesAFrame()
    {
        var source = MediaTestFixture.CreateSdrSource("thumbnail-one-run.mp4", durationSeconds: 5, audioTracks: 0);
        var destination = Path.Combine(MediaTestFixture.ScratchRoot, "thumbnail-one-run.jpg");
        var log = Path.Combine(MediaTestFixture.ScratchRoot, "thumbnail-one-run.log");
        var extractor = new FfmpegThumbnailExtractor(
            MediaTestFixture.CreateCountingStubFfmpeg("ffmpeg-one-run", log, writesFrameAt: destination),
            MediaTestFixture.Binaries.Ffprobe);

        Assert.True(extractor.TryExtract(source, destination));

        var run = Assert.Single(File.ReadAllLines(log));
        Assert.Contains("-ss 1.5 -i", run);
    }

    [Fact]
    public void TryExtract_FallsBackToFrameZeroOnce_WhenTheChosenSeekProducesNoFrame()
    {
        var source = MediaTestFixture.CreateSdrSource("thumbnail-two-runs.mp4", durationSeconds: 5, audioTracks: 0);
        var destination = Path.Combine(MediaTestFixture.ScratchRoot, "thumbnail-two-runs.jpg");
        var log = Path.Combine(MediaTestFixture.ScratchRoot, "thumbnail-two-runs.log");
        var extractor = new FfmpegThumbnailExtractor(
            MediaTestFixture.CreateCountingStubFfmpeg("ffmpeg-two-runs", log),
            MediaTestFixture.Binaries.Ffprobe);

        Assert.False(extractor.TryExtract(source, destination));

        var runs = File.ReadAllLines(log);
        Assert.Equal(2, runs.Length);
        Assert.Contains("-ss 1.5 -i", runs[0]);
        Assert.DoesNotContain("-ss", runs[1]);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void TryExtract_StillTriesTheFixedSeekAndFrameZero_WhenTheProbeFails()
    {
        var source = Path.Combine(MediaTestFixture.ScratchRoot, "thumbnail-growing.mp4");
        File.WriteAllText(source, "a recording that has not been finalised yet");
        var destination = Path.Combine(MediaTestFixture.ScratchRoot, "thumbnail-growing.jpg");
        var log = Path.Combine(MediaTestFixture.ScratchRoot, "thumbnail-growing.log");
        var extractor = new FfmpegThumbnailExtractor(
            MediaTestFixture.CreateCountingStubFfmpeg("ffmpeg-growing", log),
            MediaTestFixture.Binaries.Ffprobe);

        Assert.False(extractor.TryExtract(source, destination));

        var runs = File.ReadAllLines(log);
        Assert.Equal(2, runs.Length);
        Assert.Contains("-ss 1 -i", runs[0]);
        Assert.DoesNotContain("-ss", runs[1]);
    }

    [Fact]
    public void TryExtract_ReturnsFalse_AndLeavesNoFile_ForASourceThatIsNotAVideo()
    {
        var source = Path.Combine(MediaTestFixture.ScratchRoot, "not-a-video.mp4");
        File.WriteAllText(source, "this is not an mp4");
        var destination = Path.Combine(MediaTestFixture.ScratchRoot, "not-a-video.jpg");
        var extractor = new FfmpegThumbnailExtractor(MediaTestFixture.Binaries.Ffmpeg);

        Assert.False(extractor.TryExtract(source, destination));

        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void TryExtract_ReturnsFalse_WhenTheSourceDoesNotExist()
    {
        var extractor = new FfmpegThumbnailExtractor(MediaTestFixture.Binaries.Ffmpeg);

        Assert.False(extractor.TryExtract(
            Path.Combine(MediaTestFixture.ScratchRoot, "no-such-file.mp4"),
            Path.Combine(MediaTestFixture.ScratchRoot, "no-such-file.jpg")));
    }

    [Fact]
    public void RunBounded_KillsAProcessThatOverrunsTheTimeout()
    {
        var arguments = new[]
        {
            "-nostdin", "-loglevel", "error",
            "-re", "-f", "lavfi", "-i", "testsrc=size=64x48:rate=1",
            "-t", "3600", "-f", "null", "-",
        };

        var outcome = FfmpegRunner.RunBounded(MediaTestFixture.Binaries.Ffmpeg, arguments,
            TimeSpan.FromSeconds(1));

        Assert.False(outcome.Completed, "an overrunning ffmpeg must be reported as not completed");
        Assert.False(outcome.Succeeded);
        Assert.Contains("did not exit", outcome.StandardError);
    }

    [Fact]
    public void RunBounded_ReportsAFailedStart_WithoutThrowing()
    {
        var outcome = FfmpegRunner.RunBounded(
            Path.Combine(MediaTestFixture.ScratchRoot, "there-is-no-ffmpeg-here"),
            ["-version"], TimeSpan.FromSeconds(5));

        Assert.False(outcome.Completed);
        Assert.False(outcome.Succeeded);
        Assert.Contains("Failed to start ffmpeg", outcome.StandardError);
    }

    [Fact]
    public void RunBounded_ReportsSuccess_AndCapturesStderr()
    {
        var outcome = FfmpegRunner.RunBounded(MediaTestFixture.Binaries.Ffmpeg,
            ["-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=duration=0.2:size=64x48:rate=5",
                "-f", "null", "-"],
            TimeSpan.FromSeconds(30));

        Assert.True(outcome.Completed);
        Assert.True(outcome.Succeeded);
        Assert.Equal(0, outcome.ExitCode);

        Assert.Equal(string.Empty, outcome.StandardError.Trim());
    }

    private static string CreateBlackThenColorSource(string name)
    {
        var path = Path.Combine(MediaTestFixture.ScratchRoot, name);
        MediaTestFixture.Run(MediaTestFixture.Binaries.Ffmpeg,
        [
            "-hide_banner", "-y",
            "-f", "lavfi", "-i", "color=c=black:size=320x240:rate=30:duration=1",
            "-f", "lavfi", "-i", "color=c=red:size=320x240:rate=30:duration=4",
            "-filter_complex", "[0:v][1:v]concat=n=2:v=1:a=0[v]",
            "-map", "[v]", "-c:v", "libx264", "-g", "30", "-pix_fmt", "yuv420p", path,
        ]);
        return path;
    }
}
