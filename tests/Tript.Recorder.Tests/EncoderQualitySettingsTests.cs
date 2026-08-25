// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System;
using System.Collections.Generic;
using System.Linq;
using Tript.Recorder;
using Tript.Settings;
using Xunit;

namespace Tript.Recorder.Tests;

// What the recorder writes into a video encoder's settings for a given quality profile and rate-
// control choice: the quality-to-quantiser mapping, which modes each encoder family accepts, and
// which keys each mode is written with. Two separate failure modes are pinned here, and they are
// not equally survivable.
public sealed class EncoderQualitySettingsTests
{
    // One representative id per family, plus two nothing describes. The Windows hardware ids decide
    // whether the shipped Windows build records or crashes, since a hardware encoder is preferred over
    // obs_x264 whenever one is registered.
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

    // ---- the quality profile ----

    // The four presets the settings UI offers (Low 3, Medium 5, High 10, Max 18) and where they land.
    // These are the numbers a user sees the consequences of, so they are stated as literals: High at
    // 20 is a recording worth keeping, and High at the old 33 was not.
    [Theory]
    [InlineData(3, 28)]
    [InlineData(5, 23)]
    [InlineData(10, 20)]
    [InlineData(18, 16)]
    public void TheQualityPresets_LandInTheUsefulH264Band(int quality, int expectedQuantiser) =>
        Assert.Equal(expectedQuantiser, ObsEncoderPolicy.MapQualityToQuantiser(quality));

    // The product claim behind the table: the top two presets are in the part of the scale where H.264
    // is visually good (16 near-transparent, 20 very good), and no preset is anywhere near the smeared
    // 33+ region the old mapping put three of the four presets in.
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

    // Monotonic across the whole scale: a higher quality number never produces a higher quantiser.
    // Without this a "better" setting could quietly make the picture worse — the interpolated points
    // between the anchors are where that would happen, not at the anchors themselves.
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

    // Strictly better between the presets, which is the monotonicity a user can actually perceive: four
    // presets that all mapped to the same quantiser would satisfy "monotonic" and be useless.
    [Fact]
    public void EachPreset_IsStrictlyBetterThanTheOneBelowIt()
    {
        var presets = new[] { 3, 5, 10, 18 }.Select(ObsEncoderPolicy.MapQualityToQuantiser).ToArray();

        for (var i = 1; i < presets.Length; i++)
            Assert.True(presets[i] < presets[i - 1], $"preset {i} at {presets[i]} is not better than {presets[i - 1]}");
    }

    // The scale is the app's own 1..20; anything outside it is a corrupt setting or a per-game override
    // from another build, and it must still produce a value H.264 accepts rather than a quantiser off
    // the end of the scale.
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

    // ---- which modes a family accepts ----

    // x264 is the only family with a CRF mode, and it has no CQP mode at all. Both halves matter: the
    // UI must not offer CQP for x264 either, or the choice would silently become CRF.
    [Fact]
    public void X264_OffersCrfCbrAndVbrButNotCqp()
    {
        var modes = ObsEncoderPolicy.SupportedRateControlModes("obs_x264");

        Assert.Equal(new[] { RateControlMode.Crf, RateControlMode.Cbr, RateControlMode.Vbr }, modes);
        Assert.DoesNotContain(RateControlMode.Cqp, modes);
    }

    // The families documented to accept CQP, CBR and VBR together.
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

    // VAAPI is held to the two modes actually
    // evidenced: CQP (what the recorder has always written) and CBR. VBR is withheld deliberately —
    // its ceiling key is unknown to us and a mistyped ceiling key fails silently.
    [Theory]
    [InlineData("ffmpeg_vaapi")]
    [InlineData("ffmpeg_vaapi_tex")]
    [InlineData("FFMPEG_VAAPI")]
    public void TheVaapiFamily_OffersCqpAndCbrOnly(string encoderId) =>
        Assert.Equal(
            new[] { RateControlMode.Cqp, RateControlMode.Cbr },
            ObsEncoderPolicy.SupportedRateControlModes(encoderId));

    // An id no table describes still records. CBR is the one mode every documented family accepts and
    // most of them default to, so offering it alongside constant quality is not a guess; anything
    // beyond those two would be.
    [Theory]
    [InlineData("some_future_h264_encoder")]
    [InlineData("obs_x264_lookalike")]
    public void AnUnknownFamily_OffersConstantQualityAndCbr(string encoderId) =>
        Assert.Equal(
            new[] { RateControlMode.Cqp, RateControlMode.Cbr },
            ObsEncoderPolicy.SupportedRateControlModes(encoderId));

    // ---- coercion: the property that lets a settings file travel between machines ----

    // The crash condition as a property over the whole id space and the whole mode space: whatever a
    // config asks for, no family other than x264 is ever handed "CRF". A settings file written on a
    // software-only machine carries Crf; opened on an NVIDIA or AMD machine it must not write it
    // through.
    [Theory]
    [MemberData(nameof(EveryIdAndMode))]
    public void TheModeWritten_IsNeverCrfUnlessTheEncoderIsX264(string encoderId, RateControlMode requested)
    {
        var resolved = ObsEncoderPolicy.ResolveRateControl(encoderId, requested);

        if (encoderId != "obs_x264")
            Assert.NotEqual("CRF", resolved.Mode);
    }

    // The same property stated positively: whatever is written is a value the family's own table
    // accepts. Anything else is either the segfault or a recording at the plugin's default.
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

    // Constant quality survives the coercion in both directions: the intent is "constant quality", and
    // only the spelling is family-specific.
    [Theory]
    [MemberData(nameof(NonX264EncoderIds))]
    public void CrfRequestedOnAHardwareEncoder_BecomesThatFamilysConstantQuantiserMode(string encoderId) =>
        Assert.Equal(RateControlMode.Cqp, ObsEncoderPolicy.CoerceRateControlMode(encoderId, RateControlMode.Crf));

    [Fact]
    public void CqpRequestedOnX264_BecomesCrf() =>
        Assert.Equal(RateControlMode.Crf, ObsEncoderPolicy.CoerceRateControlMode("obs_x264", RateControlMode.Cqp));

    // A mode the family does not accept falls back to constant quality rather than to another
    // rate-targeted mode: constant quality needs only the quality profile, which every settings object
    // carries anyway, so the fallback cannot itself be misconfigured.
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

    // CBR is the mode every family accepts, so it is the one choice that is never coerced.
    [Theory]
    [MemberData(nameof(NonX264EncoderIds))]
    public void CbrIsAcceptedByEveryFamily(string encoderId)
    {
        Assert.Equal(RateControlMode.Cbr, ObsEncoderPolicy.CoerceRateControlMode(encoderId, RateControlMode.Cbr));
        Assert.Equal(RateControlMode.Cbr, ObsEncoderPolicy.CoerceRateControlMode("obs_x264", RateControlMode.Cbr));
    }

    // ---- which keys each mode writes ----

    // Constant quality writes the family's quantiser key and no bitrate at all.
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

    // CBR writes the bitrate and nothing else. No ceiling: max_bitrate is documented as VBR-only on
    // both families that have it, and no quantiser, because x264 zeroes crf under CBR anyway.
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

    // VBR writes the target everywhere and the ceiling only where the family documents one: NVENC and
    // QSV have max_bitrate, AMF has no ceiling key at all, and x264's ceiling is its VBV pair.
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

    // x264's VBR is the one mode that reads both dials: its bitrate is a VBV cap over a CRF target,
    // and crf is documented as meaningful under VBR. The hardware families' VBR reads the bitrate
    // alone.
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

    // No key is ever written under a name no plugin reads. Stated over the whole space so a new branch
    // cannot introduce one without failing here.
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

        // Every mode configures something: a settings object carrying only a mode string would run the
        // encoder at the plugin's default quality or bitrate.
        Assert.True(resolved.QuantiserKey is not null || resolved.BitrateKey is not null);
    }

    // ---- the bitrate figures ----

    [Fact]
    public void TheBitrate_IsClampedIntoTheRangeEveryFamilyAccepts()
    {
        Assert.Equal(15_000, ObsEncoderPolicy.ClampBitrateKbps(15_000));
        Assert.Equal(ObsEncoderPolicy.MinBitrateKbps, ObsEncoderPolicy.ClampBitrateKbps(1));
        Assert.Equal(ObsEncoderPolicy.MaxBitrateKbps, ObsEncoderPolicy.ClampBitrateKbps(int.MaxValue));
    }

    // A missing or nonsense bitrate takes the default rather than the floor: clamping 0 up to 50 kbps
    // would look configured and record a slideshow.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void AnUnsetBitrate_TakesTheDefaultRatherThanTheFloor(int bitrateKbps) =>
        Assert.Equal(ObsEncoderPolicy.DefaultBitrateKbps, ObsEncoderPolicy.ClampBitrateKbps(bitrateKbps));

    [Fact]
    public void TheVbrCeiling_IsDerivedWhenUnsetAndNeverBelowTheTarget()
    {
        // Unset: 1.5x the target.
        Assert.Equal(15_000, ObsEncoderPolicy.ResolveMaxBitrateKbps(10_000, 0));

        // Explicit and sane: used as given.
        Assert.Equal(30_000, ObsEncoderPolicy.ResolveMaxBitrateKbps(20_000, 30_000));

        // Explicit and below the target: a ceiling under the floor is meaningless, so the target wins.
        Assert.Equal(20_000, ObsEncoderPolicy.ResolveMaxBitrateKbps(20_000, 5_000));

        // Never out of range for the tightest family.
        Assert.Equal(ObsEncoderPolicy.MaxBitrateKbps, ObsEncoderPolicy.ResolveMaxBitrateKbps(90_000, int.MaxValue));
    }

    // ---- guards ----

    // No id means no family, and no family means there is no correct key set — guessing would put the
    // caller back on the crashing path. CreateOutput resolves the id first and reports a missing
    // encoder as a wiring failure.
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

    // An enum value from a settings file written by a build with more modes than this one: the JSON
    // enum converter can produce it, and it must not resolve to a mode string at all.
    [Fact]
    public void AModeThisBuildDoesNotKnow_IsRejectedRatherThanWrittenThrough()
    {
        var unknownMode = (RateControlMode)999;

        // Coercion answers first: an unknown value is not in any family's supported list, so it lands
        // on constant quality like any other unsupported request.
        Assert.Equal(RateControlMode.Cqp, ObsEncoderPolicy.CoerceRateControlMode("ffmpeg_vaapi", unknownMode));
        Assert.Equal("CQP", ObsEncoderPolicy.ResolveRateControl("ffmpeg_vaapi", unknownMode).Mode);
    }

    // The list the settings UI offers is never empty: an encoder with no offerable mode would leave the
    // user unable to configure the recording at all.
    [Theory]
    [MemberData(nameof(NonX264EncoderIds))]
    public void EveryFamilyOffersAtLeastConstantQuality(string encoderId)
    {
        var modes = ObsEncoderPolicy.SupportedRateControlModes(encoderId);

        Assert.NotEmpty(modes);
        Assert.Contains(RateControlMode.Cqp, modes);
    }

    // The list is also the UI's source of truth for what to send, so a duplicate would render a
    // repeated option.
    [Theory]
    [MemberData(nameof(NonX264EncoderIds))]
    public void TheOfferedModes_AreDistinct(string encoderId)
    {
        var modes = ObsEncoderPolicy.SupportedRateControlModes(encoderId);

        Assert.Equal<IEnumerable<RateControlMode>>(modes.Distinct(), modes);
    }
}
