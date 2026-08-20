// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Media.Tests;

// Pulling a still frame out of a recording, and the bounded runner underneath it. The library's grid
// draws these on an HTTP request thread, so two properties matter beyond "an image comes out": a
// wedged ffmpeg is killed rather than waited on, and a source too short for the seek still yields an
// image instead of nothing.
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
        // The frame is scaled to a fixed width, height following the source's aspect ratio: a
        // 320x240 source comes out 480x360.
        Assert.Equal("480", MediaTestFixture.ProbeValue(MediaTestFixture.Binaries.Ffprobe, destination, "v:0", "width"));
        Assert.Equal("360", MediaTestFixture.ProbeValue(MediaTestFixture.Binaries.Ffprobe, destination, "v:0", "height"));
    }

    // The frame is taken a second in, past the black or fading first frames of a real capture. A
    // recording shorter than that must still produce a card: the seek writes no file at all (see
    // FfmpegThumbnailExtractor for the measured exit codes) and the retry at frame 0 covers it.
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
    public void TryExtract_ReturnsFalse_AndLeavesNoFile_ForASourceThatIsNotAVideo()
    {
        var source = Path.Combine(MediaTestFixture.ScratchRoot, "not-a-video.mp4");
        File.WriteAllText(source, "this is not an mp4");
        var destination = Path.Combine(MediaTestFixture.ScratchRoot, "not-a-video.jpg");
        var extractor = new FfmpegThumbnailExtractor(MediaTestFixture.Binaries.Ffmpeg);

        Assert.False(extractor.TryExtract(source, destination));
        // A zero-length leftover would be served as a thumbnail by a caller that only checked for
        // the file's existence.
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

    // The guarantee the thumbnail path rests on: an ffmpeg that will not exit is killed at the
    // timeout, so it can never hold an HTTP request (or a listener worker) open indefinitely.
    [Fact]
    public void RunBounded_KillsAProcessThatOverrunsTheTimeout()
    {
        // -re paces a synthetic input at its native rate, so this ffmpeg would run for an hour at
        // nearly no CPU cost — a wedged process, without having to wedge one.
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
        // -loglevel error on a clean run says nothing; the point is that the stderr read completed
        // rather than being left dangling by the wait.
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
