// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System;
using System.Collections.Generic;
using System.Linq;
using Tript.Recorder;
using Tript.Settings;
using Xunit;

namespace Tript.Recorder.Tests;

public sealed class EncoderQualitySettingsTests
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

    public static TheoryData<string, RateControlMode> EveryIdAndMode
    {
        get
        {
            var data = new TheoryData<string, RateControlMode>();
            foreach (var id in new[]
                     {
                         "obs_x264", "obs_nvenc_h264_tex", "jim_nvenc", "ffmpeg_nvenc", "h264_texture_amf",
                         "obs_qsv11_v2", "ffmpeg_vaapi", "ffmpeg_vaapi_tex", "some_future_h264_encoder"
                     })
            {
                foreach (var mode in Enum.GetValues<RateControlMode>())
                    data.Add(id, mode);
            }

            return data;
        }
    }

    [Theory]
    [InlineData(3, 28)]
    [InlineData(5, 23)]
    [InlineData(10, 20)]
    [InlineData(18, 16)]
    public void TheQualityPresets_LandInTheUsefulH264Band(int quality, int expectedQuantiser) =>
        Assert.Equal(expectedQuantiser, ObsEncoderPolicy.MapQualityToQuantiser(quality));

    [Fact]
    public void TheTopPresets_AreInTheGoodRangeAndNoPresetIsSmeared()
    {
        var high = ObsEncoderPolicy.MapQualityToQuantiser(10);
        var max = ObsEncoderPolicy.MapQualityToQuantiser(18);

        Assert.InRange(high, 16, 22);
        Assert.InRange(max, 14, 18);

        foreach (var preset in new[] { 3, 5, 10, 18 })
            Assert.InRange(ObsEncoderPolicy.MapQualityToQuantiser(preset), 14, 28);
    }

    [Fact]
    public void TheScale_IsMonotonic_HigherQualityIsNeverAHigherQuantiser()
    {
        var previous = ObsEncoderPolicy.MapQualityToQuantiser(1);
        for (var quality = 2; quality <= 20; quality++)
        {
            var current = ObsEncoderPolicy.MapQualityToQuantiser(quality);
            Assert.True(current <= previous,
                $"quality {quality} produced quantiser {current}, worse than {previous} at quality {quality - 1}");
            previous = current;
        }
    }

    [Fact]
    public void EachPreset_IsStrictlyBetterThanTheOneBelowIt()
    {
        var presets = new[] { 3, 5, 10, 18 }.Select(ObsEncoderPolicy.MapQualityToQuantiser).ToArray();

        for (var i = 1; i < presets.Length; i++)
            Assert.True(presets[i] < presets[i - 1], $"preset {i} at {presets[i]} is not better than {presets[i - 1]}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(21)]
    [InlineData(1000)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void AnOutOfRangeQuality_StillProducesAValidQuantiser(int quality) =>
        Assert.InRange(ObsEncoderPolicy.MapQualityToQuantiser(quality), 0, 51);

    [Fact]
    public void TheScaleEnds_ClampRatherThanExtrapolate()
    {
        Assert.Equal(ObsEncoderPolicy.MapQualityToQuantiser(1), ObsEncoderPolicy.MapQualityToQuantiser(-100));
        Assert.Equal(ObsEncoderPolicy.MapQualityToQuantiser(20), ObsEncoderPolicy.MapQualityToQuantiser(100));
    }

    [Fact]
    public void X264_OffersCrfCbrAndVbrButNotCqp()
    {
        var modes = ObsEncoderPolicy.SupportedRateControlModes("obs_x264");

        Assert.Equal(new[] { RateControlMode.Crf, RateControlMode.Cbr, RateControlMode.Vbr }, modes);
        Assert.DoesNotContain(RateControlMode.Cqp, modes);
    }

    [Theory]
    [InlineData("obs_nvenc_h264_tex")]
    [InlineData("jim_nvenc")]
    [InlineData("ffmpeg_nvenc")]
    [InlineData("h264_texture_amf")]
    [InlineData("obs_qsv11_v2")]
    public void TheDocumentedHardwareFamilies_OfferCqpCbrAndVbr(string encoderId) =>
        Assert.Equal(
            new[] { RateControlMode.Cqp, RateControlMode.Cbr, RateControlMode.Vbr },
            ObsEncoderPolicy.SupportedRateControlModes(encoderId));

    [Theory]
    [InlineData("ffmpeg_vaapi")]
    [InlineData("ffmpeg_vaapi_tex")]
    [InlineData("FFMPEG_VAAPI")]
    public void TheVaapiFamily_OffersCqpAndCbrOnly(string encoderId) =>
        Assert.Equal(
            new[] { RateControlMode.Cqp, RateControlMode.Cbr },
            ObsEncoderPolicy.SupportedRateControlModes(encoderId));

    [Theory]
    [InlineData("some_future_h264_encoder")]
    [InlineData("obs_x264_lookalike")]
    public void AnUnknownFamily_OffersConstantQualityAndCbr(string encoderId) =>
        Assert.Equal(
            new[] { RateControlMode.Cqp, RateControlMode.Cbr },
            ObsEncoderPolicy.SupportedRateControlModes(encoderId));

    [Theory]
    [MemberData(nameof(EveryIdAndMode))]
    public void TheModeWritten_IsNeverCrfUnlessTheEncoderIsX264(string encoderId, RateControlMode requested)
    {
        var resolved = ObsEncoderPolicy.ResolveRateControl(encoderId, requested);

        if (encoderId != "obs_x264")
            Assert.NotEqual("CRF", resolved.Mode);
    }

    [Theory]
    [MemberData(nameof(EveryIdAndMode))]
    public void TheModeWritten_IsAlwaysOneTheFamilyAccepts(string encoderId, RateControlMode requested)
    {
        var resolved = ObsEncoderPolicy.ResolveRateControl(encoderId, requested);

        string[] accepted;
        if (encoderId == "obs_x264")
            accepted = new[] { "CRF", "CBR", "VBR" };
        else if (encoderId.Contains("vaapi", StringComparison.OrdinalIgnoreCase))
            accepted = new[] { "CQP", "CBR" };
        else
            accepted = new[] { "CQP", "CBR", "VBR" };

        Assert.Contains(resolved.Mode, accepted);
    }

    [Theory]
    [MemberData(nameof(NonX264EncoderIds))]
    public void CrfRequestedOnAHardwareEncoder_BecomesThatFamilysConstantQuantiserMode(string encoderId) =>
        Assert.Equal(RateControlMode.Cqp, ObsEncoderPolicy.CoerceRateControlMode(encoderId, RateControlMode.Crf));

    [Fact]
    public void CqpRequestedOnX264_BecomesCrf() =>
        Assert.Equal(RateControlMode.Crf, ObsEncoderPolicy.CoerceRateControlMode("obs_x264", RateControlMode.Cqp));

    [Theory]
    [InlineData("ffmpeg_vaapi")]
    [InlineData("some_future_h264_encoder")]
    public void VbrRequestedWhereItIsNotOffered_FallsBackToConstantQuality(string encoderId)
    {
        Assert.Equal(RateControlMode.Cqp, ObsEncoderPolicy.CoerceRateControlMode(encoderId, RateControlMode.Vbr));

        var resolved = ObsEncoderPolicy.ResolveRateControl(encoderId, RateControlMode.Vbr);
        Assert.Equal("CQP", resolved.Mode);
        Assert.Null(resolved.BitrateKey);
    }

    [Theory]
    [MemberData(nameof(NonX264EncoderIds))]
    public void CbrIsAcceptedByEveryFamily(string encoderId)
    {
        Assert.Equal(RateControlMode.Cbr, ObsEncoderPolicy.CoerceRateControlMode(encoderId, RateControlMode.Cbr));
        Assert.Equal(RateControlMode.Cbr, ObsEncoderPolicy.CoerceRateControlMode("obs_x264", RateControlMode.Cbr));
    }

    [Theory]
    [InlineData("obs_x264", "CRF", "crf")]
    [InlineData("obs_nvenc_h264_tex", "CQP", "cqp")]
    [InlineData("h264_texture_amf", "CQP", "cqp")]
    [InlineData("obs_qsv11_v2", "CQP", "cqp")]
    [InlineData("ffmpeg_vaapi", "CQP", "qp")]
    [InlineData("some_future_h264_encoder", "CQP", "cqp")]
    public void ConstantQuality_WritesTheFamilysQuantiserKeyOnly(string encoderId, string mode, string quantiserKey)
    {
        var resolved = ObsEncoderPolicy.ResolveRateControl(encoderId, RateControlMode.Cqp);

        Assert.Equal(mode, resolved.Mode);
        Assert.Equal(quantiserKey, resolved.QuantiserKey);
        Assert.Null(resolved.BitrateKey);
        Assert.Null(resolved.MaxBitrateKey);
    }

    [Theory]
    [InlineData("obs_x264")]
    [InlineData("obs_nvenc_h264_tex")]
    [InlineData("h264_texture_amf")]
    [InlineData("obs_qsv11_v2")]
    [InlineData("ffmpeg_vaapi")]
    public void Cbr_WritesTheBitrateAndNoCeiling(string encoderId)
    {
        var resolved = ObsEncoderPolicy.ResolveRateControl(encoderId, RateControlMode.Cbr);

        Assert.Equal("CBR", resolved.Mode);
        Assert.Equal("bitrate", resolved.BitrateKey);
        Assert.Null(resolved.QuantiserKey);
        Assert.Null(resolved.MaxBitrateKey);
    }

    [Theory]
    [InlineData("obs_nvenc_h264_tex", "max_bitrate")]
    [InlineData("jim_nvenc", "max_bitrate")]
    [InlineData("obs_qsv11_v2", "max_bitrate")]
    [InlineData("h264_texture_amf", null)]
    [InlineData("obs_x264", null)]
    public void Vbr_WritesTheCeilingOnlyWhereTheFamilyHasOne(string encoderId, string? maxBitrateKey)
    {
        var resolved = ObsEncoderPolicy.ResolveRateControl(encoderId, RateControlMode.Vbr);

        Assert.Equal("VBR", resolved.Mode);
        Assert.Equal("bitrate", resolved.BitrateKey);
        Assert.Equal(maxBitrateKey, resolved.MaxBitrateKey);
    }

    [Fact]
    public void X264Vbr_WritesBothTheQuantiserAndTheBitrate()
    {
        var x264 = ObsEncoderPolicy.ResolveRateControl("obs_x264", RateControlMode.Vbr);
        Assert.Equal("crf", x264.QuantiserKey);
        Assert.Equal("bitrate", x264.BitrateKey);

        var nvenc = ObsEncoderPolicy.ResolveRateControl("obs_nvenc_h264_tex", RateControlMode.Vbr);
        Assert.Null(nvenc.QuantiserKey);
        Assert.Equal("bitrate", nvenc.BitrateKey);
    }

    [Theory]
    [MemberData(nameof(EveryIdAndMode))]
    public void EveryKeyWritten_IsOneTheFamiliesName(string encoderId, RateControlMode requested)
    {
        var resolved = ObsEncoderPolicy.ResolveRateControl(encoderId, requested);

        if (resolved.QuantiserKey is { } quantiser)
            Assert.Contains(quantiser, new[] { "crf", "cqp", "qp" });
        if (resolved.BitrateKey is { } bitrate)
            Assert.Equal("bitrate", bitrate);
        if (resolved.MaxBitrateKey is { } max)
            Assert.Equal("max_bitrate", max);

        Assert.True(resolved.QuantiserKey is not null || resolved.BitrateKey is not null);
    }

    [Fact]
    public void TheBitrate_IsClampedIntoTheRangeEveryFamilyAccepts()
    {
        Assert.Equal(15_000, ObsEncoderPolicy.ClampBitrateKbps(15_000));
        Assert.Equal(ObsEncoderPolicy.MinBitrateKbps, ObsEncoderPolicy.ClampBitrateKbps(1));
        Assert.Equal(ObsEncoderPolicy.MaxBitrateKbps, ObsEncoderPolicy.ClampBitrateKbps(int.MaxValue));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void AnUnsetBitrate_TakesTheDefaultRatherThanTheFloor(int bitrateKbps) =>
        Assert.Equal(ObsEncoderPolicy.DefaultBitrateKbps, ObsEncoderPolicy.ClampBitrateKbps(bitrateKbps));

    [Fact]
    public void TheVbrCeiling_IsDerivedWhenUnsetAndNeverBelowTheTarget()
    {
        Assert.Equal(15_000, ObsEncoderPolicy.ResolveMaxBitrateKbps(10_000, 0));

        Assert.Equal(30_000, ObsEncoderPolicy.ResolveMaxBitrateKbps(20_000, 30_000));

        Assert.Equal(20_000, ObsEncoderPolicy.ResolveMaxBitrateKbps(20_000, 5_000));

        Assert.Equal(ObsEncoderPolicy.MaxBitrateKbps, ObsEncoderPolicy.ResolveMaxBitrateKbps(90_000, int.MaxValue));
    }

    [Fact]
    public void ANullOrEmptyId_IsRejectedByEveryEntryPoint()
    {
        Assert.Throws<ArgumentNullException>(() => ObsEncoderPolicy.SupportedRateControlModes(null!));
        Assert.Throws<ArgumentException>(() => ObsEncoderPolicy.SupportedRateControlModes(string.Empty));
        Assert.Throws<ArgumentNullException>(() => ObsEncoderPolicy.CoerceRateControlMode(null!, RateControlMode.Cqp));
        Assert.Throws<ArgumentException>(() => ObsEncoderPolicy.CoerceRateControlMode(string.Empty, RateControlMode.Cqp));
        Assert.Throws<ArgumentNullException>(() => ObsEncoderPolicy.ResolveRateControl(null!, RateControlMode.Cqp));
        Assert.Throws<ArgumentException>(() => ObsEncoderPolicy.ResolveRateControl(string.Empty, RateControlMode.Cqp));
    }

    [Fact]
    public void AModeThisBuildDoesNotKnow_IsRejectedRatherThanWrittenThrough()
    {
        var unknownMode = (RateControlMode)999;

        Assert.Equal(RateControlMode.Cqp, ObsEncoderPolicy.CoerceRateControlMode("ffmpeg_vaapi", unknownMode));
        Assert.Equal("CQP", ObsEncoderPolicy.ResolveRateControl("ffmpeg_vaapi", unknownMode).Mode);
    }

    [Theory]
    [MemberData(nameof(NonX264EncoderIds))]
    public void EveryFamilyOffersAtLeastConstantQuality(string encoderId)
    {
        var modes = ObsEncoderPolicy.SupportedRateControlModes(encoderId);

        Assert.NotEmpty(modes);
        Assert.Contains(RateControlMode.Cqp, modes);
    }

    [Theory]
    [MemberData(nameof(NonX264EncoderIds))]
    public void TheOfferedModes_AreDistinct(string encoderId)
    {
        var modes = ObsEncoderPolicy.SupportedRateControlModes(encoderId);

        Assert.Equal<IEnumerable<RateControlMode>>(modes.Distinct(), modes);
    }
}
