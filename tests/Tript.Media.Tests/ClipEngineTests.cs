// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Media;
using Xunit;

namespace Tript.Media.Tests;

// The clip engine against real files. Every fixture is generated with ffmpeg into a per-run temp
// directory, so the suite is self-contained and exercises the full shell-out path.
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
        // keyframes at 2.0 and 3.0.
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

    // Regression: production sends an empty AudioTrackAdjustments list. default(AudioTrackAdjustment)
    // has Volume=0, and FindFirst's default result used to put every unlisted track through
    // "volume=0" — every clip came out digitally silent. Unlisted tracks must pass through.
    [Fact]
    public void CreateClips_Separate_NoAdjustments_AudioPassesThrough()
    {
        var source = MediaTestFixture.CreateSdrSource("audio2-passthrough.mp4", audioTracks: 2);
        var outputDir = Path.Combine(MediaTestFixture.ScratchRoot, "clips-audio-passthrough");
        var ffmpeg = MediaTestFixture.Binaries.Ffmpeg;

        var baselineDb = new[]
        {
            MediaTestFixture.AudioRmsDb(ffmpeg, source, 0),
            MediaTestFixture.AudioRmsDb(ffmpeg, source, 1),
        };

        var engine = NewEngine();
        var paths = engine.CreateClips(new ClipRequest
        {
            SourcePath = source,
            Regions = [ClipRegion.FromSeconds(0.5, 2.5)],
            Mode = ClipMode.Separate,
            OutputPath = outputDir,
            // Intentionally no AudioTrackAdjustments: the default empty list is the production path.
        });
        var output = Assert.Single(paths);

        for (var t = 0; t < 2; t++)
        {
            var clipDb = MediaTestFixture.AudioRmsDb(ffmpeg, output, t);
            Assert.True(double.IsFinite(clipDb), $"clip track {t} must not be silent, got {clipDb:0.##} dB");
            // The fixture tone is steady, so the re-encode should land within ~2 dB of the source.
            Assert.True(
                Math.Abs(clipDb - baselineDb[t]) <= 2.0,
                $"clip track {t} ({clipDb:0.##} dB) should match source ({baselineDb[t]:0.##} dB)");
        }
    }

    // Same regression on the combine path, with two regions.
    [Fact]
    public void CreateClips_Combine_NoAdjustments_AudioPassesThrough()
    {
        var source = MediaTestFixture.CreateSdrSource("audio2-combine-passthrough.mp4", audioTracks: 2);
        var output = Path.Combine(MediaTestFixture.ScratchRoot, "clip-audio-passthrough.mp4");
        var ffmpeg = MediaTestFixture.Binaries.Ffmpeg;

        var baselineDb = new[]
        {
            MediaTestFixture.AudioRmsDb(ffmpeg, source, 0),
            MediaTestFixture.AudioRmsDb(ffmpeg, source, 1),
        };

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
            // Intentionally no AudioTrackAdjustments: the default empty list is the production path.
        });

        for (var t = 0; t < 2; t++)
        {
            var clipDb = MediaTestFixture.AudioRmsDb(ffmpeg, output, t);
            Assert.True(double.IsFinite(clipDb), $"clip track {t} must not be silent, got {clipDb:0.##} dB");
            Assert.True(
                Math.Abs(clipDb - baselineDb[t]) <= 2.0,
                $"clip track {t} ({clipDb:0.##} dB) should match source ({baselineDb[t]:0.##} dB)");
        }
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
            // tone-map fallback, select a codec that cannot carry 10-bit — the same input
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

        // The tone-map chain's stage order is the part a rewrite is most likely to lose. Assert the
        // output's pixels match a reference produced by the canonical five-stage chain, written here
        // as an independent constant: if the engine's chain order is ever changed, this reference
        // still encodes the correct order and the PSNR comparison fails.
        var reference = Path.Combine(MediaTestFixture.ScratchRoot, "hdr-tm-reference.mp4");
        MediaTestFixture.Run(MediaTestFixture.Binaries.Ffmpeg,
        [
            "-hide_banner", "-v", "error", "-y",
            "-ss", "0", "-i", source,
            "-frames:v", "1",
            "-filter_complex",
            "[0:v]zscale=t=linear:npl=75,format=gbrpf32le,zscale=p=bt709,"
            + "tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p,"
            + "eq=contrast=1.05:saturation=1.05:gamma=0.99[v]",
            "-map", "[v]",
            "-c:v", "libx264",
            reference,
        ]);

        var psnrToReference = MediaTestFixture.FirstFrameVsRawPsnrDb(
            MediaTestFixture.Binaries.Ffmpeg, output, reference, 640, 360, "hdr-tm-ref");
        Assert.True(psnrToReference > 40.0,
            $"tone-mapped output should match the canonical chain, got {psnrToReference} dB vs reference");
    }

    [Fact]
    public void CreateClips_HdrSource_ForceSdrOverridesPreservation()
    {
        var source = MediaTestFixture.CreateHdrSource("hdr-force-sdr.mkv");
        var outputDir = Path.Combine(MediaTestFixture.ScratchRoot, "clips-hdr-force-sdr");

        var output = Assert.Single(NewEngine().CreateClips(new ClipRequest
        {
            SourcePath = source,
            Regions = [ClipRegion.FromSeconds(0, 2)],
            Mode = ClipMode.Separate,
            OutputPath = outputDir,
            ForceSdr = true,
        }));

        Assert.Equal("h264", Probe("v:0", "codec_name", output));
        Assert.Equal("yuv420p", Probe("v:0", "pix_fmt", output));
        Assert.Equal("bt709", Probe("v:0", "color_transfer", output));
        Assert.Equal("bt709", Probe("v:0", "color_primaries", output));
        Assert.Equal("bt709", Probe("v:0", "color_space", output));
    }

    [Fact]
    public void CreateClips_RefusesOutputThatWouldOverwriteSource()
    {
        var source = MediaTestFixture.CreateHdrSource("hdr-no-overwrite.mkv");

        var exception = Assert.Throws<ClipSourceException>(() => NewEngine().CreateClips(new ClipRequest
        {
            SourcePath = source,
            Regions = [ClipRegion.FromSeconds(0, 1)],
            Mode = ClipMode.Combine,
            OutputPath = source,
            ForceSdr = true,
        }));

        Assert.Contains("overwrite", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(source));
    }

    // The preserve path needs a 10-bit-carrying codec. The engine's default software path is
    // libx265 (carries 10-bit) — the only one this Linux box can exercise.
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

    // A region that runs past the end of the recording is truncated at the end, not refused. It still
    // names a real piece of the file, and ffmpeg cuts exactly this clip on its own for a straddling
    // region — refusing the request turned a clip the user could have had into an error, which is what
    // the frontend's 120 s placeholder duration produced for every recording whose metadata carried no
    // endTime.
    [Fact]
    public void CreateClips_RegionStraddlingTheEnd_IsClampedToTheDuration()
    {
        var source = MediaTestFixture.CreateSdrSource("short.mp4", durationSeconds: 2);
        var engine = NewEngine();
        var outputDir = Path.Combine(MediaTestFixture.ScratchRoot, "clips-straddle");

        var paths = engine.CreateClips(new ClipRequest
        {
            SourcePath = source,
            Regions = [ClipRegion.FromSeconds(1.0, 5.0)],
            Mode = ClipMode.Separate,
            OutputPath = outputDir,
        });

        var clip = Assert.Single(paths);
        var duration = MediaTestFixture.ProbeDuration(MediaTestFixture.Binaries.Ffprobe, clip);
        // 1.0s of a 2.0s source survives the clamp; the requested 4.0s does not.
        Assert.InRange(duration, 0.9, 1.1);
    }

    // Nothing survivable is left, so the request fails rather than reaching ffmpeg — which would
    // report exit code 0 and write a 261-byte MP4 with no video stream (measured).
    [Fact]
    public void CreateClips_RegionWhollyBeyondDuration_ThrowsClearError()
    {
        var source = MediaTestFixture.CreateSdrSource("beyond.mp4", durationSeconds: 2);
        var engine = NewEngine();
        var neverClips = Path.Combine(MediaTestFixture.ScratchRoot, "never-clips-beyond");

        var ex = Assert.Throws<ClipSourceException>(() => engine.CreateClips(new ClipRequest
        {
            SourcePath = source,
            Regions = [ClipRegion.FromSeconds(60.0, 120.0)],
            Mode = ClipMode.Separate,
            OutputPath = neverClips,
        }));

        Assert.Contains("nothing to clip", ex.Message);
        // The real length is in the message, because "past the end" is only actionable if the user is
        // told where the end is.
        Assert.Contains("The recording is", ex.Message);
        Assert.Contains("long", ex.Message);
        Assert.False(Directory.Exists(neverClips), "no output directory may be created for a refused clip");
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

    // A swapped pair names exactly one interval, so it is ordered rather than refused: a timeline drag
    // whose anchor ends up after its cursor produces one for ordinary reasons, and the only remedy a
    // refusal could offer is "draw the same region the other way round".
    [Fact]
    public void CreateClips_RegionEndBeforeStart_IsOrderedAndClipped()
    {
        var source = MediaTestFixture.CreateSdrSource("reversed.mp4");
        var engine = NewEngine();
        var outputDir = Path.Combine(MediaTestFixture.ScratchRoot, "clips-reversed");

        var paths = engine.CreateClips(new ClipRequest
        {
            SourcePath = source,
            Regions = [ClipRegion.FromSeconds(3.0, 1.0)],
            Mode = ClipMode.Separate,
            OutputPath = outputDir,
        });

        var clip = Assert.Single(paths);
        var duration = MediaTestFixture.ProbeDuration(MediaTestFixture.Binaries.Ffprobe, clip);
        Assert.InRange(duration, 1.9, 2.1); // 1.0s - 3.0s, the interval the swapped pair names
    }

    // The swap does not rescue a degenerate pair: an equal start and end has no interval to order, and
    // ffmpeg reads the resulting "-t 0" as "no limit" and returns the entire recording (measured), so
    // it must never reach the argument builder.
    [Fact]
    public void CreateClips_ZeroLengthRegion_ThrowsClearError()
    {
        var source = MediaTestFixture.CreateSdrSource("degenerate.mp4");
        var engine = NewEngine();
        var neverClips = Path.Combine(MediaTestFixture.ScratchRoot, "never-clips-degenerate");

        var ex = Assert.Throws<ClipSourceException>(() => engine.CreateClips(new ClipRequest
        {
            SourcePath = source,
            Regions = [ClipRegion.FromSeconds(2.0, 2.0)],
            Mode = ClipMode.Separate,
            OutputPath = neverClips,
        }));

        Assert.Contains("nothing to clip", ex.Message);
        Assert.False(Directory.Exists(neverClips), "no output directory may be created for a refused clip");
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

    // ---- Verification 7: exit code 0 is not proof of an output file ----

    // ffmpeg exits 0 on more than one path that leaves no usable file behind. Returning the path
    // anyway hands the app a "clip" it will list, thumbnail and offer to play, all against a file
    // that is not there.
    [Fact]
    public void CreateClips_FfmpegSucceedsButWritesNoFile_Throws()
    {
        var source = MediaTestFixture.CreateSdrSource("no-output.mp4");
        var outputPath = Path.Combine(MediaTestFixture.ScratchRoot, "clips-no-output", "combined.mp4");
        var engine = new ClipEngine(
            MediaTestFixture.CreateStubFfmpeg("ffmpeg-silent"),
            new MediaProbe(MediaTestFixture.Binaries.Ffprobe));

        var ex = Assert.Throws<ClipEncodeException>(() => engine.CreateClips(new ClipRequest
        {
            SourcePath = source,
            Regions = [ClipRegion.FromSeconds(0.5, 2.5)],
            Mode = ClipMode.Combine,
            OutputPath = outputPath,
        }));

        Assert.Contains("wrote no file", ex.Message);
        Assert.False(File.Exists(outputPath));
    }

    // The same for a file that exists but is empty: a zero-byte mp4 is not a clip, and every
    // consumer of the returned path treats existence as enough.
    [Fact]
    public void CreateClips_FfmpegSucceedsButWritesAnEmptyFile_Throws()
    {
        var source = MediaTestFixture.CreateSdrSource("empty-output.mp4");
        var outputDirectory = Path.Combine(MediaTestFixture.ScratchRoot, "clips-empty-output");
        var outputPath = Path.Combine(outputDirectory, "combined.mp4");
        Directory.CreateDirectory(outputDirectory);
        var engine = new ClipEngine(
            MediaTestFixture.CreateStubFfmpeg("ffmpeg-empty", writesEmptyFileAt: outputPath),
            new MediaProbe(MediaTestFixture.Binaries.Ffprobe));

        var ex = Assert.Throws<ClipEncodeException>(() => engine.CreateClips(new ClipRequest
        {
            SourcePath = source,
            Regions = [ClipRegion.FromSeconds(0.5, 2.5)],
            Mode = ClipMode.Combine,
            OutputPath = outputPath,
        }));

        Assert.Contains("empty file", ex.Message);
    }

    // ---- Verification 8: two nearly identical regions are two clips ----

    // Separate-mode names carry the region's times, and ffmpeg runs with -y. Times rounded coarsely
    // enough that two distinct regions print the same put the second clip on top of the first: the
    // user asked for two files and got one, silently.
    [Fact]
    public void CreateClips_RegionsMillisecondsApart_DoNotShareOneOutputName()
    {
        var source = MediaTestFixture.CreateSdrSource("near-identical.mp4");
        var outputDirectory = Path.Combine(MediaTestFixture.ScratchRoot, "clips-near-identical");
        var engine = NewEngine();

        string Clip(double start, double end) => engine.CreateClips(new ClipRequest
        {
            SourcePath = source,
            Regions = [ClipRegion.FromSeconds(start, end)],
            Mode = ClipMode.Separate,
            OutputPath = outputDirectory,
        }).Single();

        // 4 ms apart: closer than the two decimals the names used to carry, and a plausible
        // adjustment to a region the user already clipped once.
        var first = Clip(1.0, 2.0);
        var second = Clip(1.004, 2.004);

        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first), "the first clip must survive the second");
        Assert.True(File.Exists(second));
        Assert.Equal(2, Directory.GetFiles(outputDirectory, "*.mp4").Length);
    }
}
