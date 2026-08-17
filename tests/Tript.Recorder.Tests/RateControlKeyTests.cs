// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Recorder;
using Xunit;

namespace Tript.Recorder.Tests;

// The per-family rate-control mapping CreateOutput writes into the video encoder's settings. This is
// the one encoder-settings mistake that is not survivable.
//
// Every other plugin-private key fails silently: a key the plugin does not read leaves it on its own
// default, and the recording still produces a plausible file at the wrong quality. rate_control on a
// hardware encoder does not fail silently. obs-ffmpeg's VAAPI encoder looks the string up in a
// NULL-terminated table of {CBR, CQP, VBR, QVBR} and, finding no match, walks onto the terminator and
// calls strcmp(NULL, "CBR") — SEGV_MAPERR, inside obs_output_initialize_encoders, from
// obs_output_start. The process dies; the user loses the session. That is the crash this mapping
// prevents, and it is why a wrong mode string here is a correctness bug rather than a quality bug.
//
// The mapping is asserted by id because that is the only thing the caller knows. Ids, not runtime
// versions: from OBS 31 several families' key sets are live at once, so "which id did we create" is
// the only answerable question. The expected values come from
// tests/Tript.Obs.IntegrationTests/EncoderSettingsKeyTable.cs, which transcribes each family's
// accepted rate_control values and its quantiser key from the specification.
//
// The quality number itself is family-independent — H.264 CRF and H.264 QP/CQP share the 0..51 scale
// — so only the mode string and the key name are in question here.
public sealed class RateControlKeyTests
{
    // Every non-x264 id the mapping can plausibly be handed. The Windows hardware ids are the ones
    // that decide whether the shipped Windows build records or crashes, since ResolveVideoEncoderId
    // prefers a hardware encoder over obs_x264 whenever one is registered. The trailing entries are
    // deliberately not in any table: an id from a plugin nobody here has seen must still resolve to a
    // mode that no known H.264 family rejects, because the alternative is the segfault above.
    public static TheoryData<string> NonX264EncoderIds =>
    [
        // NVENC texture encoders (OBS 31+ ids).
        "obs_nvenc_h264_tex",
        // NVENC legacy ids, still live alongside the texture ones.
        "jim_nvenc",
        "ffmpeg_nvenc",
        // AMD AMF.
        "h264_texture_amf",
        // Intel QSV.
        "obs_qsv11_v2",
        // VAAPI, the family that actually crashed.
        "ffmpeg_vaapi",
        "ffmpeg_vaapi_tex",
        // Unknown to every table.
        "some_future_h264_encoder",
        "obs_x264_lookalike"
    ];

    // x264 is the only family whose constant-quality mode is CRF, and the only one that names the
    // quantiser "crf". It is also the only id matched exactly rather than by substring, so an id that
    // merely contains "obs_x264" is not x264 — see TheModeIsNeverCrf_ForAnyNonX264Id.
    [Fact]
    public void X264_GetsCrfAndTheCrfKey() =>
        Assert.Equal(("CRF", "crf"), ObsRecorderSession.ResolveRateControlKeys("obs_x264"));

    // The Windows hardware families. All four accept "CQP" and all four name the quantiser "cqp":
    // NVENC under both key sets, AMF and QSV. These are the ids the Windows build resolves to in
    // practice, so a regression here is a crash on the platform the release targets.
    [Theory]
    [InlineData("obs_nvenc_h264_tex")]
    [InlineData("jim_nvenc")]
    [InlineData("ffmpeg_nvenc")]
    [InlineData("h264_texture_amf")]
    [InlineData("obs_qsv11_v2")]
    public void TheWindowsHardwareEncoders_GetCqpAndTheCqpKey(string encoderId) =>
        Assert.Equal(("CQP", "cqp"), ObsRecorderSession.ResolveRateControlKeys(encoderId));

    // VAAPI is the exception among the hardware families, and the reason the exception was missed: it
    // accepts "CQP" like the others but names the quantiser "qp", not "cqp". The specification's key
    // table has no VAAPI row at all, so nothing there would have caught it. Both the copy-path id and
    // the texture-path id are covered, since the implementation matches on the substring and the
    // texture variants share the family's keys.
    [Theory]
    [InlineData("ffmpeg_vaapi")]
    [InlineData("ffmpeg_vaapi_tex")]
    public void TheVaapiEncoders_GetCqpAndThePlainQpKey(string encoderId) =>
        Assert.Equal(("CQP", "qp"), ObsRecorderSession.ResolveRateControlKeys(encoderId));

    // The substring match is OrdinalIgnoreCase, so the family is recognised however a plugin cases its
    // id. Worth pinning rather than assuming: an Ordinal match here would send "FFMPEG_VAAPI" down the
    // default branch and write "cqp", which VAAPI does not read — a silent quality bug, not a crash,
    // and therefore the kind that ships.
    [Theory]
    [InlineData("FFMPEG_VAAPI")]
    [InlineData("Ffmpeg_VaApi")]
    [InlineData("av1_ffmpeg_VAAPI_tex")]
    public void TheVaapiMatch_IgnoresCase(string encoderId) =>
        Assert.Equal(("CQP", "qp"), ObsRecorderSession.ResolveRateControlKeys(encoderId));

    // The crash condition, asserted as a property over the whole id space rather than one id at a
    // time. No H.264 family other than x264 lists "CRF" among its accepted rate_control values, so
    // handing that string to any other encoder is the fault — for VAAPI a segfault, for the rest a
    // recording at the plugin's default quality. Stated this way, a future branch that resolves a new
    // id to x264's key set fails here even though nobody thought to add an InlineData row for it.
    [Theory]
    [MemberData(nameof(NonX264EncoderIds))]
    public void TheModeIsNeverCrf_ForAnyNonX264Id(string encoderId)
    {
        var (rateControl, _) = ObsRecorderSession.ResolveRateControlKeys(encoderId);

        Assert.NotEqual("CRF", rateControl);
    }

    // The positive half of the same property. "CQP" is the one constant-quality mode every non-x264
    // H.264 family in the table accepts — NVENC texture, NVENC legacy, AMF, QSV — and it is what the
    // VAAPI table accepts too, so it is also the only safe answer for an id no table describes.
    [Theory]
    [MemberData(nameof(NonX264EncoderIds))]
    public void TheModeIsAlwaysCqp_ForAnyNonX264Id(string encoderId)
    {
        var (rateControl, _) = ObsRecorderSession.ResolveRateControlKeys(encoderId);

        Assert.Equal("CQP", rateControl);
    }

    // The quantiser key is only ever one of the three the families name. Nothing should be able to
    // produce a fourth string: a key no plugin reads is written into the settings object, read by
    // nobody, and the encoder runs at its default quality with no error anywhere.
    [Theory]
    [MemberData(nameof(NonX264EncoderIds))]
    public void TheQualityKey_IsAlwaysOneTheFamiliesName(string encoderId)
    {
        var (_, qualityKey) = ObsRecorderSession.ResolveRateControlKeys(encoderId);

        Assert.Contains(qualityKey, new[] { "cqp", "qp" });
    }

    // No id means no family, and no family means there is no correct key set to write. Guessing would
    // put the caller back on the crashing path, so this throws instead — CreateOutput resolves the id
    // before it gets here and reports a missing encoder as a wiring failure.
    [Fact]
    public void ANullOrEmptyId_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => ObsRecorderSession.ResolveRateControlKeys(null!));
        Assert.Throws<ArgumentException>(() => ObsRecorderSession.ResolveRateControlKeys(string.Empty));
    }
}
