// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;
using Xunit;

namespace Tript.Recorder.Tests;

// The colour decision for one recording. Pure: it takes the display's HDR state, the setting, and
// what this machine registered, and answers with a canvas and an encoder. No libobs involved.
public sealed class HdrPlanTests
{
    private static readonly VideoEncoderCandidate X264 = new("obs_x264", "h264");
    private static readonly VideoEncoderCandidate Nvenc = new("obs_nvenc_h264_tex", "h264");
    private static readonly VideoEncoderCandidate NvencHevc = new("obs_nvenc_hevc_tex", "hevc");
    private static readonly VideoEncoderCandidate NvencAv1 = new("obs_nvenc_av1_tex", "av1");

    [Fact]
    public void AnSdrDisplay_RecordsSdr_EvenWithAnHdrEncoderAvailable()
    {
        var plan = HdrPlanner.Decide(
            capturedColorSpace: null,
            displayIsHdr: false, hdrEnabledInSettings: true, [X264, NvencHevc], configuredEncoderId: null);

        Assert.False(plan.UseHdr);
        Assert.Equal(ObsVideoFormat.Nv12, plan.OutputFormat);
        Assert.Equal(ObsColorSpace.Rec709, plan.ColorSpace);
        Assert.Null(plan.Profile);
    }

    [Fact]
    public void AnHdrDisplay_WithAnHevcEncoder_TakesThePqCanvasAndMain10()
    {
        var plan = HdrPlanner.Decide(
            capturedColorSpace: null,
            displayIsHdr: true, hdrEnabledInSettings: true, [X264, NvencHevc], configuredEncoderId: null);

        Assert.True(plan.UseHdr);
        Assert.Equal("obs_nvenc_hevc_tex", plan.EncoderId);
        Assert.Equal(ObsVideoFormat.P010, plan.OutputFormat);
        Assert.Equal(ObsColorSpace.Rec2100Pq, plan.ColorSpace);
        Assert.Equal("main10", plan.Profile);
    }

    // AV1 has no "main10" to set: ten bits is the profile's own baseline, and writing a profile the
    // encoder does not declare is how a family gets an unrecognised string.
    [Fact]
    public void AnAv1Encoder_TakesHdrWithoutAProfile()
    {
        var plan = HdrPlanner.Decide(
            capturedColorSpace: null,
            displayIsHdr: true, hdrEnabledInSettings: true, [X264, NvencAv1], configuredEncoderId: null);

        Assert.True(plan.UseHdr);
        Assert.Equal("obs_nvenc_av1_tex", plan.EncoderId);
        Assert.Null(plan.Profile);
    }

    // The case that makes an HDR game recordable at all on a machine that cannot encode HDR.
    [Fact]
    public void AnHdrDisplay_WithOnlyH264_RecordsSdrAndForcesTheCaptureToTonemap()
    {
        var plan = HdrPlanner.Decide(
            capturedColorSpace: null,
            displayIsHdr: true, hdrEnabledInSettings: true, [X264, Nvenc], configuredEncoderId: null);

        Assert.False(plan.UseHdr);
        Assert.True(plan.ForceSdrOnCapture);
        Assert.Contains("no registered encoder", plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TurningHdrOff_RecordsSdrAndStillTonemaps()
    {
        var plan = HdrPlanner.Decide(
            capturedColorSpace: null,
            displayIsHdr: true, hdrEnabledInSettings: false, [X264, NvencHevc], configuredEncoderId: null);

        Assert.False(plan.UseHdr);
        Assert.True(plan.ForceSdrOnCapture);
        Assert.Contains("turned off", plan.Reason, StringComparison.Ordinal);
    }

    // An SDR recording never asks the capture to tonemap: the source is already in the canvas's
    // colour space, and forcing it would be a second conversion.
    [Fact]
    public void AnHdrRecording_LeavesTheCaptureAlone()
    {
        var plan = HdrPlanner.Decide(
            capturedColorSpace: null,
            displayIsHdr: true, hdrEnabledInSettings: true, [NvencHevc], configuredEncoderId: null);

        Assert.False(plan.ForceSdrOnCapture);
    }

    [Fact]
    public void AConfiguredEncoder_IsKeptForAnSdrRecording()
    {
        var plan = HdrPlanner.Decide(
            capturedColorSpace: null,
            displayIsHdr: false, hdrEnabledInSettings: true, [X264, Nvenc], configuredEncoderId: "obs_nvenc_h264_tex");

        Assert.Equal("obs_nvenc_h264_tex", plan.EncoderId);
    }

    // Deliberately overridden rather than honoured: an H.264 encoder handed a PQ canvas writes a
    // file whose metadata promises a range the eight-bit stream does not carry.
    [Fact]
    public void AConfiguredEncoderThatCannotDoHdr_IsReplacedRatherThanUsed()
    {
        var plan = HdrPlanner.Decide(
            capturedColorSpace: null,
            displayIsHdr: true, hdrEnabledInSettings: true, [X264, NvencHevc], configuredEncoderId: "obs_x264");

        Assert.True(plan.UseHdr);
        Assert.Equal("obs_nvenc_hevc_tex", plan.EncoderId);
    }

    [Fact]
    public void AConfiguredHdrCapableEncoder_IsKeptOverTheFirstOneFound()
    {
        var plan = HdrPlanner.Decide(
            capturedColorSpace: null,
            displayIsHdr: true,
            hdrEnabledInSettings: true,
            [NvencHevc, NvencAv1],
            configuredEncoderId: "obs_nvenc_av1_tex");

        Assert.Equal("obs_nvenc_av1_tex", plan.EncoderId);
    }

    // The exact set this machine registers, in the order libobs enumerates it. The software AV1
    // encoders come first, so picking by enumeration order would record a game in software.
    [Fact]
    public void HardwareHevc_WinsOverSoftwareAv1_EvenWhenSoftwareEnumeratesFirst()
    {
        var plan = HdrPlanner.Decide(
            capturedColorSpace: null,
            displayIsHdr: true,
            hdrEnabledInSettings: true,
            [
                new VideoEncoderCandidate("ffmpeg_svt_av1", "av1"),
                new VideoEncoderCandidate("ffmpeg_aom_av1", "av1"),
                new VideoEncoderCandidate("h265_texture_amf", "hevc"),
                new VideoEncoderCandidate("obs_x264", "h264")
            ],
            configuredEncoderId: null);

        Assert.True(plan.UseHdr);
        Assert.Equal("h265_texture_amf", plan.EncoderId);
        Assert.Equal("main10", plan.Profile);
    }

    // A texture encoder takes the frame straight off the GPU; the fallback path copies it back first.
    [Fact]
    public void ATextureEncoder_WinsOverTheSameFamilysFallback()
    {
        var plan = HdrPlanner.Decide(
            capturedColorSpace: null,
            displayIsHdr: true,
            hdrEnabledInSettings: true,
            [new VideoEncoderCandidate("h265_fallback_amf", "hevc"), new VideoEncoderCandidate("h265_texture_amf", "hevc")],
            configuredEncoderId: null);

        Assert.Equal("h265_texture_amf", plan.EncoderId);
    }

    // The regression that keying off the display created. On an HDR desktop an SDR game still hands
    // over an sRGB swap chain, and putting that on a PQ canvas records black just as surely as the
    // other way round — the mismatch is symmetric, so the source has the last word.
    [Fact]
    public void AnSdrGameOnAnHdrDisplay_RecordsSdr()
    {
        var plan = HdrPlanner.Decide(
            capturedColorSpace: ObsSourceColorSpace.Srgb,
            displayIsHdr: true,
            hdrEnabledInSettings: true,
            [X264, NvencHevc],
            configuredEncoderId: null);

        Assert.False(plan.UseHdr);
        Assert.Equal(ObsVideoFormat.Nv12, plan.OutputFormat);
        Assert.Contains("Srgb", plan.Reason, StringComparison.Ordinal);
    }

    // And the case that started all of this: Overwatch presents FP16 scRGB.
    [Fact]
    public void AnHdrGame_RecordsHdr_EvenWhenTheDisplayProbeSaysOtherwise()
    {
        var plan = HdrPlanner.Decide(
            capturedColorSpace: ObsSourceColorSpace.Scrgb709,
            displayIsHdr: false,
            hdrEnabledInSettings: true,
            [X264, NvencHevc],
            configuredEncoderId: null);

        Assert.True(plan.UseHdr);
        Assert.Equal(ObsVideoFormat.P010, plan.OutputFormat);
        Assert.Equal(ObsColorSpace.Rec2100Pq, plan.ColorSpace);
    }

    // Srgb16F is high-precision SDR, not HDR. Treating "16F" as a synonym for HDR would put an
    // ordinary source on a PQ canvas.
    [Theory]
    [InlineData(ObsSourceColorSpace.Srgb, false)]
    [InlineData(ObsSourceColorSpace.Srgb16F, false)]
    [InlineData(ObsSourceColorSpace.Extended709, true)]
    [InlineData(ObsSourceColorSpace.Scrgb709, true)]
    public void OnlyTheExtendedSpacesCountAsHdr(ObsSourceColorSpace space, bool hdr) =>
        Assert.Equal(hdr, HdrPlanner.IsHdr(space));

    [Fact]
    public void NoRegisteredEncoder_IsRefusedRatherThanPlanned()
    {
        Assert.Throws<ArgumentException>(() =>
            HdrPlanner.Decide(null, displayIsHdr: false, hdrEnabledInSettings: true, [], configuredEncoderId: null));
    }

    [Theory]
    [InlineData("hevc", true)]
    [InlineData("HEVC", true)]
    [InlineData("av1", true)]
    [InlineData("h264", false)]
    public void HdrCapabilityFollowsTheCodec(string codec, bool capable) =>
        Assert.Equal(capable, HdrPlanner.IsHdrCapable(new VideoEncoderCandidate("id", codec)));

    // The probe answers on both platforms rather than throwing — a marshalling mistake in the
    // display-config structs would surface here rather than at the start of a recording. Off Windows
    // there is no single "is the desktop in HDR" switch, so the answer is always false and every
    // Linux recording takes the SDR path with tonemapping.
    [Fact]
    public void TheDisplayProbe_Answers_AndIsFalseOffWindows()
    {
        var isHdr = HdrDisplayProbe.AnyDisplayIsHdr();

        if (!OperatingSystem.IsWindows())
            Assert.False(isHdr);
    }

    // These sizes are the whole correctness of the probe, and getting one wrong is silent: the query
    // succeeds, the array is walked at the wrong stride, and every display reads as SDR. That is not
    // hypothetical — DISPLAYCONFIG_RATIONAL declared as a ulong padded the target struct from 48 to
    // 56 bytes and made an HDR monitor invisible, which sent an HDR game to an SDR canvas and
    // recorded a black video. Runs everywhere: the layout is the platform's either way.
    [Fact]
    public void TheDisplayConfigStructs_MatchTheWindowsHeaders()
    {
        foreach (var (name, actual, expected) in HdrDisplayProbe.NativeStructSizes())
            Assert.Equal((name, expected), (name, actual));
    }
}
