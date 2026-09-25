// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Media.Tests;

public sealed class PqRelabellerTests
{
    private static string Encode(string name, params string[] videoCodec)
    {
        var path = Path.Combine(MediaTestFixture.ScratchRoot, name);
        var args = new List<string>
        {
            "-hide_banner", "-y",
            "-f", "lavfi", "-i", "testsrc2=duration=1:size=128x72:rate=30",
            "-f", "lavfi", "-i", "sine=frequency=1000:duration=1:sample_rate=48000",
            "-map", "0:v", "-map", "1:a",
        };
        args.AddRange(videoCodec);
        args.AddRange(["-pix_fmt", "yuv420p", "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709",
            "-c:a", "aac", path]);
        MediaTestFixture.Run(MediaTestFixture.Binaries.Ffmpeg, args);
        return path;
    }

    private static MediaProbe Probe() => new(MediaTestFixture.Binaries.Ffprobe);

    [Theory]
    [InlineData("h264", new[] { "-c:v", "libx264" })]
    [InlineData("hevc", new[] { "-c:v", "libx265", "-x265-params", "log-level=error" })]
    [InlineData("av1", new[] { "-c:v", "libaom-av1", "-cpu-used", "8", "-b:v", "200k" })]
    public void ARecordingIsRelabelledAsPqWithoutReencodingItsPictureOrDroppingAudio(string codec, string[] encoder)
    {
        var path = Encode($"pq-relabel-{codec}.mp4", encoder);
        var before = new MediaProbe(MediaTestFixture.Binaries.Ffprobe).Probe(path);

        var outcome = PqRelabeller.Relabel(MediaTestFixture.Binaries.Ffmpeg, Probe(), path);

        Assert.True(outcome.Labelled, outcome.Failure);
        var after = Probe().Probe(path);
        Assert.Equal(codec, after.CodecName);
        Assert.Equal("smpte2084", after.ColorTransfer);
        Assert.Equal("bt2020", after.ColorPrimaries);
        Assert.Equal("bt709", after.ColorSpace);
        Assert.Equal(before.PixelFormat, after.PixelFormat);
        Assert.Equal(before.AudioStreamCount, after.AudioStreamCount);
        Assert.Equal(before.DurationSeconds, after.DurationSeconds, precision: 1);
        Assert.Empty(Directory.EnumerateFiles(MediaTestFixture.ScratchRoot, $".pq-relabel-{codec}.mp4*"));
    }

    [Fact]
    public void ARecordingAlreadyLabelledPq_IsLeftAsItIs()
    {
        var path = Encode("pq-already.mp4", "-c:v", "libx264");
        Assert.True(PqRelabeller.Relabel(MediaTestFixture.Binaries.Ffmpeg, Probe(), path).Labelled);
        var labelledAt = File.GetLastWriteTimeUtc(path);

        var outcome = PqRelabeller.Relabel(MediaTestFixture.Binaries.Ffmpeg, Probe(), path);

        Assert.True(outcome.Labelled);
        Assert.Equal(labelledAt, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void ACodecWithoutAMetadataFilter_IsReportedAndTheFileIsUntouched()
    {
        var path = Encode("pq-mpeg4.mp4", "-c:v", "mpeg4");
        var bytes = File.ReadAllBytes(path);

        var outcome = PqRelabeller.Relabel(MediaTestFixture.Binaries.Ffmpeg, Probe(), path);

        Assert.False(outcome.Labelled);
        Assert.Contains("mpeg4", outcome.Failure);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void AFailingFfmpeg_LeavesTheRecordingAsItWasAndNoTemporaryFile()
    {
        var path = Encode("pq-failing.mp4", "-c:v", "libx264");
        var bytes = File.ReadAllBytes(path);
        var stub = MediaTestFixture.CreateStubFfmpeg("pq-failing-ffmpeg");

        var outcome = PqRelabeller.Relabel(stub, Probe(), path);

        Assert.False(outcome.Labelled);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Empty(Directory.EnumerateFiles(MediaTestFixture.ScratchRoot, ".pq-failing.mp4*"));
    }

    [Theory]
    [InlineData("h264", "h264_metadata=colour_primaries=9:transfer_characteristics=16:matrix_coefficients=1")]
    [InlineData("hevc", "hevc_metadata=colour_primaries=9:transfer_characteristics=16:matrix_coefficients=1")]
    [InlineData("av1", "av1_metadata=color_primaries=9:transfer_characteristics=16:matrix_coefficients=1")]
    public void TheBitstreamIsTaggedBt2020PqWithTheBt709MatrixLibobsConvertedWith(string codec, string filter)
    {
        var arguments = PqRelabeller.Arguments(codec, "in.mp4", "out.mp4");

        Assert.NotNull(arguments);
        Assert.Contains(filter, arguments);
        Assert.Contains("copy", arguments);
    }
}
