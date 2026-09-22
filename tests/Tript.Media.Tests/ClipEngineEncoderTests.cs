// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Media;
using Xunit;

namespace Tript.Media.Tests;

public sealed class ClipEngineEncoderTests
{
    private static readonly VideoEncoder Unavailable =
        new("h264_tript_unavailable", true, [], ["-hwaccel", "auto"], [], string.Empty);

    private static string Probe(string entry, string key, string path) =>
        MediaTestFixture.ProbeValue(MediaTestFixture.Binaries.Ffprobe, path, entry, key);

    private static ClipEngine Engine(VideoEncoderSelector encoders) =>
        new(MediaTestFixture.Binaries.Ffmpeg, new MediaProbe(MediaTestFixture.Binaries.Ffprobe), encoders);

    [Fact]
    public void FailingGpuEncoder_RetriesOnTheCpu_AndIsDemotedForLaterClips()
    {
        var source = MediaTestFixture.CreateSdrSource("gpu-fallback.mp4");
        var outputPath = Path.Combine(MediaTestFixture.ScratchRoot, "clips-gpu-fallback", "combined.mp4");
        var selector = new VideoEncoderSelector([Unavailable], _ => new FfmpegOutcome(true, 0, string.Empty));
        Assert.Same(Unavailable, selector.Current);

        var paths = Engine(selector).CreateClips(new ClipRequest
        {
            SourcePath = source,
            Regions = [ClipRegion.FromSeconds(0.5, 2.0), ClipRegion.FromSeconds(3.0, 4.0)],
            Mode = ClipMode.Combine,
            OutputPath = outputPath,
            EncoderFamily = "libx264",
        });

        var output = Assert.Single(paths);
        Assert.Equal("h264", Probe("v:0", "codec_name", output));
        Assert.Same(VideoEncoder.Software, selector.Current);
    }

    [Fact]
    public void FailingSource_IsNotBlamedOnTheGpuEncoder()
    {
        var source = Path.Combine(MediaTestFixture.ScratchRoot, "not-a-video.mp4");
        File.WriteAllText(source, "not a video");
        var selector = new VideoEncoderSelector([Unavailable], _ => new FfmpegOutcome(true, 0, string.Empty));
        Assert.Same(Unavailable, selector.Current);

        Assert.ThrowsAny<ClipException>(() => Engine(selector).CreateClips(new ClipRequest
        {
            SourcePath = source,
            Regions = [ClipRegion.FromSeconds(0.0, 1.0)],
            Mode = ClipMode.Separate,
            OutputPath = Path.Combine(MediaTestFixture.ScratchRoot, "clips-bad-source"),
        }));

        Assert.Same(Unavailable, selector.Current);
    }

    [Fact]
    public void PlatformSelector_ProducesAPlayableSdrClip_WithWhicheverEncoderWorksHere()
    {
        var source = MediaTestFixture.CreateSdrSource("platform-encoder.mp4");
        var outputDir = Path.Combine(MediaTestFixture.ScratchRoot, "clips-platform-encoder");
        var selector = new VideoEncoderSelector(MediaTestFixture.Binaries.Ffmpeg);

        var paths = Engine(selector).CreateClips(new ClipRequest
        {
            SourcePath = source,
            Regions = [ClipRegion.FromSeconds(0.5, 2.5)],
            Mode = ClipMode.Separate,
            OutputPath = outputDir,
            EncoderFamily = "libx264",
        });

        var output = Assert.Single(paths);
        Assert.Equal("h264", Probe("v:0", "codec_name", output));
        Assert.Contains(Probe("v:0", "pix_fmt", output), new[] { "yuv420p", "nv12" });
        Assert.Equal("bt709", Probe("v:0", "color_transfer", output));
        var duration = double.Parse(Probe("format", "duration", output), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(duration, 1.8, 2.3);
    }

    [Fact]
    public void PlatformSelector_ToneMapsAnHdrSourceToSdr_OnWhicheverPathWorksHere()
    {
        var source = MediaTestFixture.CreateHdrSource("platform-hdr.mkv");
        var outputDir = Path.Combine(MediaTestFixture.ScratchRoot, "clips-platform-hdr");
        var selector = new VideoEncoderSelector(MediaTestFixture.Binaries.Ffmpeg);

        var paths = Engine(selector).CreateClips(new ClipRequest
        {
            SourcePath = source,
            Regions = [ClipRegion.FromSeconds(0.0, 2.0)],
            Mode = ClipMode.Separate,
            OutputPath = outputDir,
            EncoderFamily = "libx264",
        });

        var output = Assert.Single(paths);
        Assert.Equal("h264", Probe("v:0", "codec_name", output));
        Assert.Equal("bt709", Probe("v:0", "color_transfer", output));
        Assert.Equal("bt709", Probe("v:0", "color_primaries", output));
    }
}
