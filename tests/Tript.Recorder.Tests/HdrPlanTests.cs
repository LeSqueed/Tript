// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;
using Xunit;

namespace Tript.Recorder.Tests;

public sealed class HdrPlanTests
{
    private static readonly VideoEncoderCandidate X264 = new("obs_x264", "h264");
    private static readonly VideoEncoderCandidate Nvenc = new("obs_nvenc_h264_tex", "h264");
    private static readonly VideoEncoderCandidate NvencHevc = new("obs_nvenc_hevc_tex", "hevc");
    private static readonly VideoEncoderCandidate NvencAv1 = new("obs_nvenc_av1_tex", "av1");
    private static readonly VideoEncoderCandidate VaapiHevc = new("hevc_ffmpeg_vaapi_tex", "hevc");

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

    [Fact]
    public void AnSdrProbeWhileADisplayIsInHdrMode_RecordsHdr()
    {
        var plan = HdrPlanner.Decide(
            capturedColorSpace: ObsSourceColorSpace.Srgb,
            displayIsHdr: true,
            hdrEnabledInSettings: true,
            [X264, NvencHevc],
            configuredEncoderId: null);

        Assert.True(plan.UseHdr);
        Assert.Equal(ObsVideoFormat.P010, plan.OutputFormat);
        Assert.Equal(ObsColorSpace.Rec2100Pq, plan.ColorSpace);
        Assert.Contains("HDR mode", plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ASucceededSdrProbe_DoesNotVetoAnHdrDisplay()
    {
        var withoutProbe = HdrPlanner.Decide(
            capturedColorSpace: null, displayIsHdr: true, hdrEnabledInSettings: true,
            [NvencAv1], configuredEncoderId: null);
        var withSdrProbe = HdrPlanner.Decide(
            capturedColorSpace: ObsSourceColorSpace.Srgb, displayIsHdr: true, hdrEnabledInSettings: true,
            [NvencAv1], configuredEncoderId: null);

        Assert.True(withoutProbe.UseHdr);
        Assert.Equal(withoutProbe.UseHdr, withSdrProbe.UseHdr);
    }

    [Fact]
    public void AnSdrSourceWithNoHdrDisplay_RecordsSdr()
    {
        var plan = HdrPlanner.Decide(
            capturedColorSpace: ObsSourceColorSpace.Srgb,
            displayIsHdr: false,
            hdrEnabledInSettings: true,
            [X264, NvencHevc],
            configuredEncoderId: null);

        Assert.False(plan.UseHdr);
        Assert.Equal(ObsVideoFormat.Nv12, plan.OutputFormat);
        Assert.Contains("Srgb", plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnSdrRecording_WithNoConfiguredEncoder_PrefersHardwareOverX264()
    {
        var plan = HdrPlanner.Decide(
            capturedColorSpace: ObsSourceColorSpace.Srgb,
            displayIsHdr: false,
            hdrEnabledInSettings: true,
            [X264, new VideoEncoderCandidate("h264_fallback_amf", "h264"), Nvenc],
            configuredEncoderId: null);

        Assert.False(plan.UseHdr);
        Assert.Equal("obs_nvenc_h264_tex", plan.EncoderId);
    }

    [Fact]
    public void AnSdrRecording_WithOnlyX264_UsesX264()
    {
        var plan = HdrPlanner.Decide(
            capturedColorSpace: ObsSourceColorSpace.Srgb,
            displayIsHdr: false,
            hdrEnabledInSettings: true,
            [X264],
            configuredEncoderId: null);

        Assert.Equal("obs_x264", plan.EncoderId);
    }

    [Fact]
    public void AnSdrRecording_KeepsRegistrationOrderWithinTheSameRank()
    {
        var plan = HdrPlanner.Decide(
            capturedColorSpace: ObsSourceColorSpace.Srgb,
            displayIsHdr: false,
            hdrEnabledInSettings: true,
            [X264, new VideoEncoderCandidate("h264_texture_amf", "h264"), new VideoEncoderCandidate("av1_texture_amf", "av1")],
            configuredEncoderId: null);

        Assert.Equal("h264_texture_amf", plan.EncoderId);
    }

    [Theory]
    [InlineData("obs_nvenc_h264_tex", 0)]
    [InlineData("h264_texture_amf", 0)]
    [InlineData("obs_qsv11_v2", 1)]
    [InlineData("obs_x264", 2)]
    public void HardwarePreference_RanksTextureEncodersFirst(string id, int rank) =>
        Assert.Equal(rank, HdrPlanner.HardwarePreference(new VideoEncoderCandidate(id, "h264")));

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

    [Fact]
    public void AVaapiHevcEncoder_LeavesTheProfileToOptForMain10ItselfOnP010()
    {
        var plan = HdrPlanner.Decide(
            capturedColorSpace: ObsSourceColorSpace.Extended709,
            displayIsHdr: false, hdrEnabledInSettings: true, [X264, VaapiHevc], configuredEncoderId: null);

        Assert.True(plan.UseHdr);
        Assert.Equal("hevc_ffmpeg_vaapi_tex", plan.EncoderId);
        Assert.Null(plan.Profile);
    }

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

    [Fact]
    public void TheDisplayProbe_Answers_AndIsFalseOffWindows()
    {
        var isHdr = HdrDisplayProbe.AnyDisplayIsHdr();

        if (!OperatingSystem.IsWindows())
            Assert.False(isHdr);
    }

    [Fact]
    public void TheDisplayConfigStructs_MatchTheWindowsHeaders()
    {
        foreach (var (name, actual, expected) in HdrDisplayProbe.NativeStructSizes())
            Assert.Equal((name, expected), (name, actual));
    }
}
