// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Recorder;
using Xunit;

namespace Tript.Recorder.Tests;

public sealed class RateControlKeyTests
{
    public static TheoryData<string> NonX264EncoderIds =>
    [
        "obs_nvenc_h264_tex",

        "jim_nvenc",
        "ffmpeg_nvenc",

        "h264_texture_amf",

        "obs_qsv11_v2",

        "ffmpeg_vaapi",
        "ffmpeg_vaapi_tex",

        "some_future_h264_encoder",
        "obs_x264_lookalike"
    ];

    [Fact]
    public void X264_GetsCrfAndTheCrfKey() =>
        Assert.Equal(("CRF", "crf"), ObsEncoderPolicy.ResolveRateControlKeys("obs_x264"));

    [Theory]
    [InlineData("obs_nvenc_h264_tex")]
    [InlineData("jim_nvenc")]
    [InlineData("ffmpeg_nvenc")]
    [InlineData("h264_texture_amf")]
    [InlineData("obs_qsv11_v2")]
    public void TheWindowsHardwareEncoders_GetCqpAndTheCqpKey(string encoderId) =>
        Assert.Equal(("CQP", "cqp"), ObsEncoderPolicy.ResolveRateControlKeys(encoderId));

    [Theory]
    [InlineData("ffmpeg_vaapi")]
    [InlineData("ffmpeg_vaapi_tex")]
    public void TheVaapiEncoders_GetCqpAndThePlainQpKey(string encoderId) =>
        Assert.Equal(("CQP", "qp"), ObsEncoderPolicy.ResolveRateControlKeys(encoderId));

    [Theory]
    [InlineData("FFMPEG_VAAPI")]
    [InlineData("Ffmpeg_VaApi")]
    [InlineData("av1_ffmpeg_VAAPI_tex")]
    public void TheVaapiMatch_IgnoresCase(string encoderId) =>
        Assert.Equal(("CQP", "qp"), ObsEncoderPolicy.ResolveRateControlKeys(encoderId));

    [Theory]
    [MemberData(nameof(NonX264EncoderIds))]
    public void TheModeIsNeverCrf_ForAnyNonX264Id(string encoderId)
    {
        var (rateControl, _) = ObsEncoderPolicy.ResolveRateControlKeys(encoderId);

        Assert.NotEqual("CRF", rateControl);
    }

    [Theory]
    [MemberData(nameof(NonX264EncoderIds))]
    public void TheModeIsAlwaysCqp_ForAnyNonX264Id(string encoderId)
    {
        var (rateControl, _) = ObsEncoderPolicy.ResolveRateControlKeys(encoderId);

        Assert.Equal("CQP", rateControl);
    }

    [Theory]
    [MemberData(nameof(NonX264EncoderIds))]
    public void TheQualityKey_IsAlwaysOneTheFamiliesName(string encoderId)
    {
        var (_, qualityKey) = ObsEncoderPolicy.ResolveRateControlKeys(encoderId);

        Assert.Contains(qualityKey, new[] { "cqp", "qp" });
    }

    [Fact]
    public void ANullOrEmptyId_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => ObsEncoderPolicy.ResolveRateControlKeys(null!));
        Assert.Throws<ArgumentException>(() => ObsEncoderPolicy.ResolveRateControlKeys(string.Empty));
    }
}
