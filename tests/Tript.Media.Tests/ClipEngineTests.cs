// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Media;
using Xunit;

namespace Tript.Media.Tests;

// The clip engine against real files. Every fixture is generated with ffmpeg into a per-run temp
// directory, so the suite is self-contained and exercises the full shell-out path. These are the
// brief's verification items: a playable real clip, an exact-time cut, combine vs separate file
// counts, per-track audio volume/mute, HDR handling, and failure reporting.
public class ClipEngineTests
{
    private readonly (string Ffmpeg, string Ffprobe) _b = MediaTestFixture.Binaries;

    private static ClipEngine NewEngine() =>
        new(MediaTestFixture.Binaries.Ffmpeg, new MediaProbe(MediaTestFixture.Binaries.Ffprobe));

    private static string Probe(string entry, string key, string path) =>
        MediaTestFixture.ProbeValue(MediaTestFixture.Binaries.Ffprobe, path, entry, key);

    // ---- Verification 1: a real clip is produced and playable ----

    [Fact]
    public void CreateClips_SingleRegion_ProducesPlayableMp4()
    {
        var source = MediaTestFixture.CreateSdrSource("playable.mp4");
        var outputDir = Path.Combine(MediaTestFixture.ScratchRoot, "clips-playable");

        var engine = NewEngine();
        var paths = engine.CreateClips(new ClipRequest
        {
            SourcePath = source,
            Regions = [ClipRegion.FromSeconds(0.5, 2.5)],
            Mode = ClipMode.Separate,
            OutputPath = outputDir,
        });

        var clip = Assert.Single(paths);
        Assert.True(File.Exists(clip), "clip file must exist");
        Assert.True(new FileInfo(clip).Length > 0, "clip must not be empty");

        Assert.Equal("h264", Probe("v:0", "codec_name", clip));
        Assert.Equal("aac", Probe("a:0", "codec_name", clip));
        Assert.Equal("320", Probe("v:0", "width", clip));
        Assert.Equal("240", Probe("v:0", "height", clip));

        var duration = MediaTestFixture.ProbeDuration(MediaTestFixture.Binaries.Ffprobe, clip);
        Assert.InRange(duration, 1.9, 2.1); // 2.0s region
    }

    // ---- Verification 2: exact-time cut, not keyframe-aligned ----

    [Fact]
    public void CreateClips_CutAtNonKeyframeTime_FirstFrameIsExactRequestedTime()
    {
        var source = MediaTestFixture.CreateLosslessSource("exact.mp4");
        var outputDir = Path.Combine(MediaTestFixture.ScratchRoot, "clips-exact");

        var engine = NewEngine();
        var paths = engine.CreateClips(new ClipRequest
        {
            SourcePath = source,
            Regions = [ClipRegion.FromSeconds(2.233, 3.5)],
            Mode = ClipMode.Separate,
            OutputPath = outputDir,
        });
        var output = Assert.Single(paths);

        // The source has keyframes at 0, 1, 2, 3s. The region starts at 2.233s, between the
        // keyframes at 2.0 and 3.0. If the clip were keyframe-aligned its first frame would be the
        // frame at 2.0s; exact cutting means it is the frame at 2.233s.
        //
        // The clip is re-encoded lossily, so its first frame cannot be hash-equal to the source
        // frame at the cut time. But a re-encode of the same frame stays visually identical (~44 dB
        // PSNR, measured), while a different frame — the keyframe at 2.0s — is ~20 dB apart from
        // the cut-time frame. Asserting the clip's first frame is far closer to the cut-time frame
        // than to the keyframe frame is therefore the exactness proof.
        var ffmpeg = MediaTestFixture.Binaries.Ffmpeg;
        var psnrToCut = MediaTestFixture.FirstFramePsnrDb(ffmpeg, output, "2.233", source, "exact-cut");
        var psnrToKeyframe = MediaTestFixture.FirstFramePsnrDb(ffmpeg, output, "2.0", source, "exact-key");

        // The cut-time frame is a lossy re-encode of the same frame: >30 dB is a clear match.
        Assert.True(psnrToCut > 30.0, $"clip first frame should match the frame at 2.233s, got {psnrToCut} dB");
        // A keyframe-aligned cut would put the 2.0s frame here: <25 dB with the cut frame ~20 dB
        // from it. Asserting a wide margin makes the test robust.
        Assert.True(psnrToKeyframe < 25.0, $"clip first frame should not match the frame at 2.0s, got {psnrToKeyframe} dB");
        Assert.True(psnrToCut - psnrToKeyframe > 10.0,
            $"cut-time frame ({psnrToCut} dB) must be clearly closer than keyframe frame ({psnrToKeyframe} dB)");
    }

    // ---- Verification 3: combine produces one file, separate produces N ----

    [Fact]
    public void CreateClips_Combine_TwoRegions_ProduceOneFileWithSummedDuration()
    {
        var source = MediaTestFixture.CreateSdrSource("combine.mp4", audioTracks: 1);
        var output = Path.Combine(MediaTestFixture.ScratchRoot, "clip-combined.mp4");

        var engine = NewEngine();
        var paths = engine.CreateClips(new ClipRequest
        {
            SourcePath = source,
            Regions =
            [
                ClipRegion.FromSeconds(0.5, 2.0),
                ClipRegion.FromSeconds(3.0, 4.5),
            ],
            Mode = ClipMode.Combine,
            OutputPath = output,
        });

        var clip = Assert.Single(paths);
        var duration = MediaTestFixture.ProbeDuration(MediaTestFixture.Binaries.Ffprobe, clip);
        // 1.5 + 1.5 = 3.0s.
        Assert.InRange(duration, 2.9, 3.1);
    }

    [Fact]
    public void CreateClips_Separate_TwoRegions_ProduceTwoFiles()
    {
        var source = MediaTestFixture.CreateSdrSource("separate.mp4");
        var directory = Path.Combine(MediaTestFixture.ScratchRoot, "clips-separate");

        var engine = NewEngine();
        var paths = engine.CreateClips(new ClipRequest
        {
            SourcePath = source,
            Regions =
            [
                ClipRegion.FromSeconds(0.5, 2.0),
                ClipRegion.FromSeconds(3.0, 4.5),
            ],
            Mode = ClipMode.Separate,
            OutputPath = directory,
        });

        Assert.Equal(2, paths.Count);
        foreach (var path in paths)
        {
            Assert.True(File.Exists(path));
            Assert.True(new FileInfo(path).Length > 0);
        }
    }

    // ---- Verification 4: audio track handling ----

    [Fact]
    public void CreateClips_Separate_VolumeAndMute_AreReflectedInAudioTracks()
    {
        var source = MediaTestFixture.CreateSdrSource("audio2.mp4", audioTracks: 2);
        var outputDir = Path.Combine(MediaTestFixture.ScratchRoot, "clips-audio");

        // Baseline RMS of the untouched source track 0.
        var baselineDb = MediaTestFixture.AudioRmsDb(MediaTestFixture.Binaries.Ffmpeg, source, 0);

        var engine = NewEngine();
        var paths = engine.CreateClips(new ClipRequest
        {
            SourcePath = source,
            Regions = [ClipRegion.FromSeconds(0.5, 2.5)],
            Mode = ClipMode.Separate,
            OutputPath = outputDir,
            AudioTrackAdjustments =
            [
                new AudioTrackAdjustment(0, Volume: 0.5),
                AudioTrackAdjustment.Mute(1),
            ],
        });
        var output = Assert.Single(paths);

        // Volume 0.5 is exactly -6.02 dB. The output track 0 must be ~6 dB below baseline.
        var outputTrack0Db = MediaTestFixture.AudioRmsDb(MediaTestFixture.Binaries.Ffmpeg, output, 0);
        Assert.InRange(outputTrack0Db, baselineDb - 7.0, baselineDb - 5.0);

        // The muted track reads as silence (-inf).
        var outputTrack1Db = MediaTestFixture.AudioRmsDb(MediaTestFixture.Binaries.Ffmpeg, output, 1);
        Assert.Equal(double.NegativeInfinity, outputTrack1Db);
    }

    // ---- Verification 5: HDR handling ----

    [Fact]
    public void CreateClips_HdrSource_ToneMapsToSdrBt709()
    {
        var source = MediaTestFixture.CreateHdrSource("hdr.mkv");
        var outputDir = Path.Combine(MediaTestFixture.ScratchRoot, "clips-hdr-tm");

        // Confirm the fixture really is HDR before clipping it.
        Assert.Equal("smpte2084", Probe("v:0", "color_transfer", source));
        Assert.Equal("yuv420p10le", Probe("v:0", "pix_fmt", source));

        var engine = NewEngine();
        var paths = engine.CreateClips(new ClipRequest
        {
            SourcePath = source,
            Regions = [ClipRegion.FromSeconds(0.0, 2.0)],
            Mode = ClipMode.Separate,
            OutputPath = outputDir,
            // A uniform HDR source preserves by default (libx265 carries 10-bit). To exercise the
            // spec's tone-map fallback, select a codec that cannot carry 10-bit — the same input
            // that flips the decision in production when a non-HEVC encoder is used.
            EncoderFamily = "libx264",
        });
        var output = Assert.Single(paths);

        // Tone-mapped to SDR: yuv420p, BT.709 tags.
        Assert.Equal("h264", Probe("v:0", "codec_name", output));
        Assert.Equal("yuv420p", Probe("v:0", "pix_fmt", output));
        Assert.Equal("bt709", Probe("v:0", "color_transfer", output));
        Assert.Equal("bt709", Probe("v:0", "color_primaries", output));
        Assert.Equal("bt709", Probe("v:0", "color_space", output));

        // The tone-map chain's stage order is the spec's "most likely to be lost" part. Assert the
        // output's pixels match a reference produced by the canonical five-stage chain, written here
        // as an independent constant — if the engine's chain order is ever changed, this reference
        // still encodes the spec's order and the PSNR comparison fails.
        var reference = Path.Combine(MediaTestFixture.ScratchRoot, "hdr-tm-reference.mp4");
        MediaTestFixture.Run(MediaTestFixture.Binaries.Ffmpeg,
        [
            "-hide_banner", "-v", "error", "-y",
            "-ss", "0", "-i", source,
            "-frames:v", "1",
            "-filter_complex",
            "[0:v]zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,"
            + "tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p[v]",
            "-map", "[v]",
            "-c:v", "libx264",
            reference,
        ]);

        var psnrToReference = MediaTestFixture.FirstFrameVsRawPsnrDb(
            MediaTestFixture.Binaries.Ffmpeg, output, reference, 640, 360, "hdr-tm-ref");
        Assert.True(psnrToReference > 40.0,
            $"tone-mapped output should match the canonical chain, got {psnrToReference} dB vs reference");
    }

    // The spec's preserve path needs a 10-bit-carrying codec. The engine's default software path is
    // libx265 (carries 10-bit) — the only one this Linux box can exercise. Preserve is therefore the
    // decision for an HDR source with a uniform transfer, and the output must be 10-bit, tagged
    // bt2020nc/bt2020 with the source transfer carried through, and main10 profile.
    [Fact]
    public void CreateClips_HdrSource_Preserves10BitWithTags()
    {
        var source = MediaTestFixture.CreateHdrSource("hdr-preserve.mkv");
        var outputDir = Path.Combine(MediaTestFixture.ScratchRoot, "clips-hdr-preserve");

        var engine = NewEngine();
        var paths = engine.CreateClips(new ClipRequest
        {
            SourcePath = source,
            Regions = [ClipRegion.FromSeconds(0.0, 2.0)],
            Mode = ClipMode.Separate,
            OutputPath = outputDir,
        });
        var output = Assert.Single(paths);

        Assert.Equal("hevc", Probe("v:0", "codec_name", output));
        Assert.Equal("yuv420p10le", Probe("v:0", "pix_fmt", output));
        Assert.Equal("Main 10", Probe("v:0", "profile", output));
        Assert.Equal("smpte2084", Probe("v:0", "color_transfer", output));
        Assert.Equal("bt2020", Probe("v:0", "color_primaries", output));
        Assert.Equal("bt2020nc", Probe("v:0", "color_space", output));
    }

    // ---- Verification 5b: SDR output is tagged BT.709 ----

    [Fact]
    public void CreateClips_SdrSource_TagsOutputBt709()
    {
        var source = MediaTestFixture.CreateSdrSource("sdr-tag.mp4");
        var outputDir = Path.Combine(MediaTestFixture.ScratchRoot, "clips-sdr-tagged");

        var engine = NewEngine();
        var paths = engine.CreateClips(new ClipRequest
        {
            SourcePath = source,
            Regions = [ClipRegion.FromSeconds(0.5, 2.5)],
            Mode = ClipMode.Separate,
            OutputPath = outputDir,
        });
        var output = Assert.Single(paths);

        Assert.Equal("bt709", Probe("v:0", "color_transfer", output));
        Assert.Equal("bt709", Probe("v:0", "color_primaries", output));
        Assert.Equal("bt709", Probe("v:0", "color_space", output));
    }

    // Combine mode also applies per-track volume/mute across the joined audio.
    [Fact]
    public void CreateClips_Combine_VolumeAndMute_AreReflectedAcrossRegions()
    {
        var source = MediaTestFixture.CreateSdrSource("audio-combine.mp4", audioTracks: 2);
        var output = Path.Combine(MediaTestFixture.ScratchRoot, "clip-audio-combine.mp4");

        var baselineDb = MediaTestFixture.AudioRmsDb(MediaTestFixture.Binaries.Ffmpeg, source, 0);

        var engine = NewEngine();
        engine.CreateClips(new ClipRequest
        {
            SourcePath = source,
            Regions =
            [
                ClipRegion.FromSeconds(0.5, 2.0),
                ClipRegion.FromSeconds(3.0, 4.5),
            ],
            Mode = ClipMode.Combine,
            OutputPath = output,
            AudioTrackAdjustments =
            [
                new AudioTrackAdjustment(0, Volume: 0.5),
                AudioTrackAdjustment.Mute(1),
            ],
        });

        var outputTrack0Db = MediaTestFixture.AudioRmsDb(MediaTestFixture.Binaries.Ffmpeg, output, 0);
        Assert.InRange(outputTrack0Db, baselineDb - 7.0, baselineDb - 5.0);

        var outputTrack1Db = MediaTestFixture.AudioRmsDb(MediaTestFixture.Binaries.Ffmpeg, output, 1);
        Assert.Equal(double.NegativeInfinity, outputTrack1Db);
    }

    // The pipeline reports progress as it runs; a progress callback that fires is the observable
    // contract.
    [Fact]
    public void CreateClips_ReportsProgress()
    {
        var source = MediaTestFixture.CreateSdrSource("progress.mp4");
        var outputDir = Path.Combine(MediaTestFixture.ScratchRoot, "clips-progress");

        var stages = new List<string>();
        var engine = NewEngine();
        engine.CreateClips(new ClipRequest
        {
            SourcePath = source,
            Regions = [ClipRegion.FromSeconds(0.5, 1.5)],
            Mode = ClipMode.Separate,
            OutputPath = outputDir,
            Progress = p => stages.Add(p.Stage),
        });

        Assert.NotEmpty(stages);
        Assert.Contains(stages, s => s.Contains("separate"));
    }

    // ---- Verification 6: failure reporting ----

    [Fact]
    public void CreateClips_MissingSource_ThrowsClearError()
    {
        var engine = NewEngine();
        var ex = Assert.Throws<ClipSourceException>(() => engine.CreateClips(new ClipRequest
        {
            SourcePath = Path.Combine(MediaTestFixture.ScratchRoot, "does-not-exist.mp4"),
            Regions = [ClipRegion.FromSeconds(0, 1)],
            Mode = ClipMode.Separate,
            OutputPath = Path.Combine(MediaTestFixture.ScratchRoot, "never-clips"),
        }));

        Assert.Contains("does not exist", ex.Message);
    }

    [Fact]
    public void CreateClips_RegionBeyondDuration_ThrowsClearError()
    {
        var source = MediaTestFixture.CreateSdrSource("short.mp4", durationSeconds: 2);
        var engine = NewEngine();

        var ex = Assert.Throws<ClipSourceException>(() => engine.CreateClips(new ClipRequest
        {
            SourcePath = source,
            Regions = [ClipRegion.FromSeconds(1.0, 5.0)],
            Mode = ClipMode.Separate,
            OutputPath = Path.Combine(MediaTestFixture.ScratchRoot, "never-clips"),
        }));

        Assert.Contains("beyond", ex.Message);
    }

    [Fact]
    public void CreateClips_NoRegions_ThrowsClearError()
    {
        var source = MediaTestFixture.CreateSdrSource("noregions.mp4");
        var engine = NewEngine();

        var ex = Assert.Throws<ClipSourceException>(() => engine.CreateClips(new ClipRequest
        {
            SourcePath = source,
            Regions = [],
            Mode = ClipMode.Separate,
            OutputPath = Path.Combine(MediaTestFixture.ScratchRoot, "never-clips"),
        }));

        Assert.Contains("At least one region", ex.Message);
    }

    [Fact]
    public void CreateClips_RegionEndNotAfterStart_ThrowsClearError()
    {
        var source = MediaTestFixture.CreateSdrSource("reversed.mp4");
        var engine = NewEngine();

        var ex = Assert.Throws<ClipSourceException>(() => engine.CreateClips(new ClipRequest
        {
            SourcePath = source,
            Regions = [ClipRegion.FromSeconds(2.0, 1.0)],
            Mode = ClipMode.Separate,
            OutputPath = Path.Combine(MediaTestFixture.ScratchRoot, "never-clips"),
        }));

        Assert.Contains("not after", ex.Message);
    }

    // A missing ffmpeg binary must be a clear error, not a silent empty output.
    [Fact]
    public void CreateClips_MissingFfmpeg_ThrowsClearError()
    {
        var probe = new MediaProbe(MediaTestFixture.Binaries.Ffprobe);
        var engine = new ClipEngine("/nonexistent/ffmpeg", probe);
        var source = MediaTestFixture.CreateSdrSource("missingffmpeg.mp4");

        Assert.Throws<ClipSourceException>(() => engine.CreateClips(new ClipRequest
        {
            SourcePath = source,
            Regions = [ClipRegion.FromSeconds(0.5, 1.5)],
            Mode = ClipMode.Separate,
            OutputPath = Path.Combine(MediaTestFixture.ScratchRoot, "never-clips"),
        }));
    }

}
