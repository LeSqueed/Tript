// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Media;
using Xunit;

namespace Tript.Media.Tests;

public sealed class VideoEncoderSelectorTests
{
    private static readonly FfmpegOutcome Works = new(true, 0, string.Empty);
    private static readonly FfmpegOutcome Fails = new(true, 1, "Error while opening encoder");

    private static VideoEncoder Fake(string name) =>
        new(name, true, ["-fake"], ["-hwaccel", "auto"], [], string.Empty);

    [Fact]
    public void Current_PicksTheFirstCandidateThatEncodes_AndProbesOnlyOnce()
    {
        var probed = new List<string>();
        var selector = new VideoEncoderSelector([Fake("a"), Fake("b"), Fake("c")], candidate =>
        {
            probed.Add(candidate.Name);
            return candidate.Name == "b" ? Works : Fails;
        });

        Assert.Equal("b", selector.Current.Name);
        Assert.Equal("b", selector.Current.Name);
        Assert.Equal(["a", "b"], probed);
    }

    [Fact]
    public void Current_FallsBackToLibx264_WhenNoCandidateEncodes()
    {
        var selector = new VideoEncoderSelector([Fake("a"), Fake("b")], _ => Fails);

        Assert.Same(VideoEncoder.Software, selector.Current);
    }

    [Fact]
    public void Demote_MovesToTheNextUnprobedCandidate_ThenToLibx264()
    {
        var probed = new List<string>();
        var selector = new VideoEncoderSelector([Fake("a"), Fake("b")], candidate =>
        {
            probed.Add(candidate.Name);
            return Works;
        });

        var first = selector.Current;
        selector.Demote(first);
        Assert.Equal("b", selector.Current.Name);

        selector.Demote(selector.Current);
        Assert.Same(VideoEncoder.Software, selector.Current);
        Assert.Equal(["a", "b"], probed);
    }

    [Fact]
    public void Demote_IgnoresTheSoftwareEncoder()
    {
        var selector = VideoEncoderSelector.SoftwareOnly();

        selector.Demote(VideoEncoder.Software);

        Assert.Same(VideoEncoder.Software, selector.Current);
    }

    [Fact]
    public void ProbeArgs_EncodeATinySyntheticClipWithTheEncodersOwnArguments()
    {
        var args = VideoEncoderSelector.ProbeArgs(VideoEncoder.Amf).ToList();

        Assert.Contains("lavfi", args);
        Assert.Equal("h264_amf", args[args.IndexOf("-c:v") + 1]);
        Assert.Contains("-qp_p", args);
        Assert.Equal(["-f", "null", "-"], args.TakeLast(3));
        Assert.DoesNotContain("-hwaccel", args);
    }

    [Fact]
    public void ProbeArgs_Vaapi_OpensTheGivenRenderNodeAndUploadsFrames()
    {
        var args = VideoEncoderSelector.ProbeArgs(VideoEncoder.VaapiOn("/dev/dri/renderD129")).ToList();

        Assert.True(args.IndexOf("-vaapi_device") < args.IndexOf("-i"));
        Assert.Equal("/dev/dri/renderD129", args[args.IndexOf("-vaapi_device") + 1]);
        Assert.Equal("format=nv12,hwupload", args[args.IndexOf("-vf") + 1]);
    }

    [Theory]
    [InlineData(true, "h264_amf")]
    [InlineData(false, "h264_vaapi")]
    public void PlatformHardware_OffersEveryMajorVendor(bool windows, string platformEncoder)
    {
        var encoders = VideoEncoder.PlatformHardware(windows, ["/dev/dri/renderD128"]);
        var names = encoders.Select(encoder => encoder.Name).ToList();

        Assert.Contains("h264_nvenc", names);
        Assert.Contains("h264_qsv", names);
        Assert.Contains(platformEncoder, names);
        Assert.All(encoders, encoder => Assert.True(encoder.IsHardware));
    }

    [Fact]
    public void PlatformHardware_OnLinux_OffersVaapiOncePerRenderNode_InOrder()
    {
        var vaapi = VideoEncoder.PlatformHardware(windows: false, ["/dev/dri/renderD128", "/dev/dri/renderD129"])
            .Where(encoder => encoder.Name == "h264_vaapi")
            .ToList();

        Assert.Equal(["/dev/dri/renderD128", "/dev/dri/renderD129"], vaapi.Select(encoder => encoder.Device));
    }

    [Fact]
    public void PlatformHardware_OnLinux_OffersNoVaapi_WithoutARenderNode()
    {
        Assert.DoesNotContain(VideoEncoder.PlatformHardware(windows: false, []),
            encoder => encoder.Name == "h264_vaapi");
    }

    [Fact]
    public void Current_TriesTheNextRenderNode_WhenTheFirstCannotEncode()
    {
        var probed = new List<string?>();
        var selector = new VideoEncoderSelector(
            [VideoEncoder.VaapiOn("/dev/dri/renderD128"), VideoEncoder.VaapiOn("/dev/dri/renderD129")],
            candidate =>
            {
                probed.Add(candidate.Device);
                return candidate.Device == "/dev/dri/renderD129" ? Works : Fails;
            });

        Assert.Equal("/dev/dri/renderD129", selector.Current.Device);
        Assert.Equal(["/dev/dri/renderD128", "/dev/dri/renderD129"], probed);
    }

    [Fact]
    public void Demote_OfOneRenderNode_MovesToTheNextRenderNode()
    {
        var selector = new VideoEncoderSelector(
            [VideoEncoder.VaapiOn("/dev/dri/renderD128"), VideoEncoder.VaapiOn("/dev/dri/renderD129")],
            _ => Works);

        selector.Demote(selector.Current);

        Assert.Equal("/dev/dri/renderD129", selector.Current.Device);
    }

    [Fact]
    public void GpuToneMapping_ProbesOnce_AndStaysOffAfterDemotion()
    {
        var probes = 0;
        var selector = new VideoEncoderSelector([], _ => Fails, () =>
        {
            probes++;
            return Works;
        });

        Assert.True(selector.GpuToneMapping);
        Assert.True(selector.GpuToneMapping);
        selector.DemoteToneMapping();
        Assert.False(selector.GpuToneMapping);
        Assert.Equal(1, probes);
    }

    [Fact]
    public void GpuToneMapping_IsOff_WhenTheProbeFailsOrIsAbsent()
    {
        Assert.False(new VideoEncoderSelector([], _ => Fails, () => Fails).GpuToneMapping);
        Assert.False(VideoEncoderSelector.SoftwareOnly().GpuToneMapping);
    }

    [Fact]
    public void ToneMapProbeArgs_FeedsAnHdrTaggedFrameThroughTheGpuChainOnVulkan()
    {
        var args = VideoEncoderSelector.ToneMapProbeArgs().ToList();

        Assert.Equal("vulkan", args[args.IndexOf("-init_hw_device") + 1]);
        Assert.Contains("smpte2084", args[args.IndexOf("-i") + 1]);
        Assert.StartsWith("libplacebo=", args[args.IndexOf("-vf") + 1]);
    }
}
