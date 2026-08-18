// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Obs.IntegrationTests;

// Creating, configuring and identifying encoders against the real library, and the availability
// probes that decide what a recorder can offer on a given machine. The machine this suite runs on
// has x264 and the VAAPI family; it has no NVIDIA or Intel hardware, so the NVENC and QSV ids are
// exactly the "available on other machines" case that is being proven here to report as
// unavailable.
public sealed class ObsEncoderTests
{
    private const string X264Id = "obs_x264";
    private const string AacId = "ffmpeg_aac";
    private const string VaapiId = "ffmpeg_vaapi";
    private const string Av1VaapiId = "av1_ffmpeg_vaapi";

    private static ObsSession StartSession()
    {
        var session = ObsSession.StartWithSourceTypes();
        return session;
    }

    // ---- availability ----

    [Fact]
    public void TheEncodersThisMachineExposes_AreRegistered()
    {
        using var session = StartSession();

        var ids = ObsEncoder.EnumerateTypeIds();

        Assert.Contains(X264Id, ids);
        Assert.Contains(AacId, ids);
        Assert.Contains(VaapiId, ids);
        Assert.Contains(Av1VaapiId, ids);

        // The codec probe and the enumeration agree.
        Assert.Equal("h264", ObsEncoder.GetTypeCodec(X264Id));
        Assert.Equal("aac", ObsEncoder.GetTypeCodec(AacId));
    }

    [Fact]
    public void TheAvailabilityProbe_IsThatTheCodecIsNotNull()
    {
        using var session = StartSession();

        // Unregistered — measured on this box: no module registers these ids, which is the point.
        Assert.False(ObsEncoder.IsTypeRegistered("tript_no_such_encoder"));
        Assert.Null(ObsEncoder.GetTypeCodec("tript_no_such_encoder"));
        Assert.False(ObsEncoder.IsTypeRegistered("obs_nvenc_h264_tex"));
        Assert.False(ObsEncoder.IsTypeRegistered("obs_qsv11_v2"));

        // The type probe is NOT a substitute: an unknown id reports Audio (0) rather than anything
        // recognisable, so trusting it would call the unavailable available.
        Assert.Equal(ObsEncoderType.Audio, ObsEncoder.GetType("tript_no_such_encoder"));
    }

    [Fact]
    public void TheUnavailableIds_AreMissingFromTheEnumeration()
    {
        using var session = StartSession();

        var ids = ObsEncoder.EnumerateTypeIds();

        Assert.DoesNotContain("obs_nvenc_h264_tex", ids);
        Assert.DoesNotContain("obs_qsv11_v2", ids);
    }

    [Fact]
    public void TheUnavailablePath_ReportsNoDefaultsAndNoProperties()
    {
        using var session = StartSession();

        Assert.Null(ObsEncoder.GetTypeDefaults("obs_nvenc_h264_tex"));
        Assert.Null(ObsEncoder.GetTypeDefaults("obs_qsv11_v2"));
        Assert.Empty(ObsEncoder.EnumerateTypeProperties("obs_nvenc_h264_tex"));
        Assert.Empty(ObsEncoder.EnumerateTypeProperties("obs_qsv11_v2"));
    }

    [Fact]
    public void CreatingAnUnregisteredEncoder_IsRefusedRatherThanGivenAPlaceholder()
    {
        using var session = StartSession();

        // Both create functions hand back a non-null placeholder for an unknown id — a context
        // with a null codec that encodes nothing — so null-checking the result proves nothing.
        var videoFailure = Assert.Throws<ObsException>(() => ObsEncoder.CreateVideo("tript_no_such_encoder", "ghost"));
        Assert.Contains("tript_no_such_encoder", videoFailure.Message, StringComparison.Ordinal);

        var audioFailure = Assert.Throws<ObsException>(() => ObsEncoder.CreateAudio("tript_no_such_encoder", "ghost-aac"));
        Assert.Contains("tript_no_such_encoder", audioFailure.Message, StringComparison.Ordinal);
    }

    // ---- creation and identity ----

    [Fact]
    public void ACreatedVideoEncoder_ReportsTheTypeIdNameCodecAndType()
    {
        using var session = StartSession();
        using var encoder = ObsEncoder.CreateVideo(X264Id, "video encoder");

        Assert.Equal(X264Id, encoder.Id);
        Assert.Equal("video encoder", encoder.Name);
        Assert.Equal("h264", encoder.Codec);
        Assert.Equal(ObsEncoderType.Video, encoder.Type);
        Assert.Equal(ObsEncoderCaps.DynBitrate | ObsEncoderCaps.Roi, encoder.Caps);
    }

    [Fact]
    public void ACreatedAudioEncoder_ReportsTheTypeIdNameCodecAndType()
    {
        using var session = StartSession();
        using var encoder = ObsEncoder.CreateAudio(AacId, "audio encoder", mixerIndex: 2);

        Assert.Equal(AacId, encoder.Id);
        Assert.Equal("audio encoder", encoder.Name);
        Assert.Equal("aac", encoder.Codec);
        Assert.Equal(ObsEncoderType.Audio, encoder.Type);
        Assert.Equal(2u, encoder.MixerIndex);
    }

    [Fact]
    public void TheCapabilityProbes_AgreeForTheRegisteredIds()
    {
        using var session = StartSession();

        Assert.Equal(ObsEncoderCaps.DynBitrate | ObsEncoderCaps.Roi, ObsEncoder.GetTypeCaps(X264Id));
        Assert.Equal(ObsEncoderCaps.Internal, ObsEncoder.GetTypeCaps(VaapiId));
    }

    [Fact]
    public void TheTypeLevelAndInstanceLevelCaps_Agree()
    {
        using var session = StartSession();
        using var encoder = ObsEncoder.CreateVideo(X264Id, "caps");

        Assert.Equal(ObsEncoder.GetTypeCaps(X264Id), encoder.Caps);
    }

    [Fact]
    public void AnEncodersName_CanBeChangedAfterCreation()
    {
        using var session = StartSession();
        using var encoder = ObsEncoder.CreateVideo(X264Id, "first name");

        encoder.Name = "second name";

        Assert.Equal("second name", encoder.Name);
    }

    [Fact]
    public void ANewEncoder_IsNeitherActiveNorFailed()
    {
        using var session = StartSession();
        using var encoder = ObsEncoder.CreateAudio(AacId, "idle");

        Assert.False(encoder.IsActive);
        Assert.Null(encoder.LastError);
        Assert.Equal(0u, encoder.EncodedFrames);
    }

    [Fact]
    public void AnEmptyOrNullTypeId_IsRejectedBeforeItReachesLibobs()
    {
        using var session = StartSession();

        // Not a nicety: the create functions dereference the id without checking it, so a null id
        // takes the process down rather than returning null.
        Assert.Throws<ArgumentNullException>(() => ObsEncoder.CreateVideo(null!, "named"));
        Assert.Throws<ArgumentException>(() => ObsEncoder.CreateVideo(string.Empty, "named"));
        Assert.Throws<ArgumentNullException>(() => ObsEncoder.CreateVideo(X264Id, null!));
        Assert.Throws<ArgumentException>(() => ObsEncoder.CreateVideo(X264Id, string.Empty));
    }

    // ---- settings ----

    // The settings object is shared with the encoder rather than copied into it — measured, and
    // the reason a caller must not treat its own reference as private after creation. This is the
    // assertion that makes the round-trip tests meaningful: a settings value that never reached the
    // encoder would still read back through the caller's own object.
    [Fact]
    public void SettingsGivenAtCreation_RemainTheEncodersOwnSettingsObject()
    {
        using var session = StartSession();

        using var settings = new ObsSettings();
        settings.SetString("rate_control", "CRF");
        settings.SetInt("crf", 22);

        using var encoder = ObsEncoder.CreateVideo(X264Id, "shared settings", settings);

        using var readBack = encoder.GetSettings();
        Assert.Equal("CRF", readBack.GetString("rate_control"));
        Assert.Equal(22, readBack.GetInt("crf"));

        // Written through the caller's own reference, after creation, with no Update call.
        settings.SetInt("crf", 30);

        using var again = encoder.GetSettings();
        Assert.Equal(30, again.GetInt("crf"));
    }

    [Fact]
    public void UpdatingAnEncoder_ChangesWhatItsSettingsReadBack()
    {
        using var session = StartSession();

        using var initial = new ObsSettings();
        initial.SetString("rate_control", "CRF");
        initial.SetInt("crf", 22);
        using var encoder = ObsEncoder.CreateVideo(X264Id, "updatable", initial);

        using var update = new ObsSettings();
        update.SetInt("crf", 30);
        encoder.Update(update);

        using var readBack = encoder.GetSettings();
        Assert.Equal(30, readBack.GetInt("crf"));

        // Update merges over the existing settings rather than replacing them.
        Assert.Equal("CRF", readBack.GetString("rate_control"));
    }

    // ---- type defaults and properties ----

    // The type defaults are what a plugin falls back on when a key has no user value — measured,
    // and they are exactly the property list with the specified defaults: CBR, 6000 Kbps,
    // veryfast, an empty profile and tune, and the 11 keys the property list names.
    [Fact]
    public void TheX264TypeDefaults_MatchTheSpecifiedDefaults()
    {
        using var session = StartSession();

        using var defaults = ObsEncoder.GetTypeDefaults(X264Id);
        Assert.NotNull(defaults);

        Assert.Equal(6000, defaults!.GetInt("bitrate"));
        Assert.False(defaults.GetBool("use_bufsize"));
        Assert.Equal(6000, defaults.GetInt("buffer_size"));
        Assert.Equal(23, defaults.GetInt("crf"));
        Assert.Equal("CBR", defaults.GetString("rate_control"));
        Assert.Equal("veryfast", defaults.GetString("preset"));
        Assert.Equal(string.Empty, defaults.GetString("profile"));
        Assert.Equal(string.Empty, defaults.GetString("tune"));

        // Defaults alone are not user values: an encoder asked whether a setting was configured
        // must not be told yes because a default exists.
        Assert.False(defaults.HasUserValue("bitrate"));
        Assert.True(defaults.HasDefaultValue("bitrate"));
    }

    // The audio encoder's defaults object exists and carries the AAC bitrate default.
    [Fact]
    public void TheAacTypeDefaults_CarryTheBitrateDefault()
    {
        using var session = StartSession();

        using var defaults = ObsEncoder.GetTypeDefaults(AacId);
        Assert.NotNull(defaults);

        Assert.Equal(128, defaults!.GetInt("bitrate"));
    }

    // The property list is the plugin-authoritative account of what it reads. This is the readback
    // that outranks a settings-bag round-trip: x264's property list is its whole key surface, and a
    // key the plugin reads but this binding never lets a caller write would show up here as a
    // property with no path to be set.
    [Fact]
    public void TheX264PropertyList_NamesExactlyTheKeysThePluginReads()
    {
        using var session = StartSession();

        var properties = ObsEncoder.EnumerateTypeProperties(X264Id);
        var names = properties.Select(property => property.Name).ToArray();

        Assert.Equal(
            ["rate_control", "bitrate", "use_bufsize", "buffer_size", "crf", "keyint_sec", "preset", "profile", "tune", "x264opts", "repeat_headers"],
            names);
    }

    [Fact]
    public void TheX264RateControlChoices_AreCbrAbrVbrCrf()
    {
        using var session = StartSession();

        var rateControl = ObsEncoder.EnumerateTypeProperties(X264Id)
            .Single(property => property.Name == "rate_control");

        Assert.Equal(ObsPropertyType.List, rateControl.Type);
        Assert.Equal(
            ["CBR", "ABR", "VBR", "CRF"],
            rateControl.Items.Select(item => item.Value).Cast<string>().ToArray());
        Assert.All(rateControl.Items, item => Assert.Equal(ObsComboFormat.String, item.Format));
    }

    [Fact]
    public void TheVaapiFamily_OffersMaxrateAndQpRatherThanMaxBitrateAndCqp()
    {
        using var session = StartSession();

        var properties = ObsEncoder.EnumerateTypeProperties(VaapiId);
        var names = properties.Select(property => property.Name).ToArray();

        Assert.Contains("vaapi_device", names);
        Assert.Contains("rate_control", names);
        Assert.Contains("maxrate", names);
        Assert.Contains("qp", names);
        Assert.Contains("keyint_sec", names);
        Assert.Contains("ffmpeg_opts", names);

        // The keys this family does NOT use, which a transcription from the NVENC/x264 table would
        // invent. max_bitrate and cqp are simply not read by the VAAPI plugin.
        Assert.DoesNotContain("max_bitrate", names);
        Assert.DoesNotContain("cqp", names);
    }

    // ---- binding and video geometry ----

    [Fact]
    public void AVideoEncoderBeforeBinding_ReportsZeroDimensions()
    {
        using var session = StartSession();
        using var encoder = ObsEncoder.CreateVideo(X264Id, "unbound");

        Assert.Equal(0u, encoder.Width);
        Assert.Equal(0u, encoder.Height);
        Assert.False(encoder.ScalingEnabled);
    }

    [Fact]
    public void BindingToVideo_MakesTheDimensionsReportTheMixSize()
    {
        using var session = StartSession();
        using var encoder = ObsEncoder.CreateVideo(X264Id, "bound");

        session.Runtime.TryGetVideoHandle(out var video);
        Assert.NotEqual(nint.Zero, video);
        encoder.BindToVideo(video);

        Assert.Equal(1280u, encoder.Width);
        Assert.Equal(720u, encoder.Height);
    }

    [Fact]
    public void ScaledSize_ReadsBackAfterBinding()
    {
        using var session = StartSession();
        using var encoder = ObsEncoder.CreateVideo(X264Id, "scaled");

        session.Runtime.TryGetVideoHandle(out var video);
        encoder.BindToVideo(video);

        encoder.SetScaledSize(640, 360);

        Assert.True(encoder.ScalingEnabled);
        Assert.Equal(640u, encoder.Width);
        Assert.Equal(360u, encoder.Height);

        // 0,0 disables and returns to the mix size.
        encoder.SetScaledSize(0, 0);
        Assert.False(encoder.ScalingEnabled);
        Assert.Equal(1280u, encoder.Width);
        Assert.Equal(720u, encoder.Height);
    }

    [Fact]
    public void GpuScaleType_ReadsBackWhatWasSet()
    {
        using var session = StartSession();
        using var encoder = ObsEncoder.CreateVideo(X264Id, "gpu scaled");

        Assert.False(encoder.GpuScalingEnabled);
        Assert.Equal(ObsScaleType.Disable, encoder.GpuScaleType);

        encoder.SetGpuScaleType(ObsScaleType.Bicubic);

        Assert.True(encoder.GpuScalingEnabled);
        Assert.Equal(ObsScaleType.Bicubic, encoder.GpuScaleType);
    }

    [Fact]
    public void TheFrameRateDivisor_RoundTripsBeforeAndAfterBinding()
    {
        using var session = StartSession();
        using var encoder = ObsEncoder.CreateVideo(X264Id, "divisor");

        Assert.Equal(1u, encoder.FrameRateDivisor);

        Assert.True(encoder.SetFrameRateDivisor(2));
        Assert.Equal(2u, encoder.FrameRateDivisor);

        session.Runtime.TryGetVideoHandle(out var video);
        encoder.BindToVideo(video);

        Assert.True(encoder.SetFrameRateDivisor(3));
        Assert.Equal(3u, encoder.FrameRateDivisor);
    }

    [Fact]
    public void PreferredVideoFormatAndColourSpace_RoundTrip()
    {
        using var session = StartSession();
        using var encoder = ObsEncoder.CreateVideo(X264Id, "preferences");

        Assert.Equal(ObsVideoFormat.None, encoder.PreferredVideoFormat);

        encoder.PreferredVideoFormat = ObsVideoFormat.Nv12;
        Assert.Equal(ObsVideoFormat.Nv12, encoder.PreferredVideoFormat);

        // The colour space and range getters read back what was set even without GPU scaling —
        // measured. The header's "only with GPU scaling" is about whether the encoder honours it,
        // not whether the value is stored.
        encoder.PreferredColorSpace = ObsColorSpace.Rec2100Pq;
        Assert.Equal(ObsColorSpace.Rec2100Pq, encoder.PreferredColorSpace);

        encoder.PreferredRange = ObsVideoRange.Full;
        Assert.Equal(ObsVideoRange.Full, encoder.PreferredRange);
    }

    // ---- audio readbacks ----

    [Fact]
    public void AnAudioEncoder_BeforeBindingReportsNoSampleRate()
    {
        using var session = StartSession();
        using var encoder = ObsEncoder.CreateAudio(AacId, "unbound audio");

        Assert.Equal(0u, encoder.SampleRate);
    }

    [Fact]
    public void BindingToAudio_MakesTheSampleRateReportTheMixRate()
    {
        using var session = StartSession();
        using var encoder = ObsEncoder.CreateAudio(AacId, "bound audio");

        Assert.True(session.Runtime.TryGetAudioHandle(out var audio));
        encoder.BindToAudio(audio);

        // The sample rate the mix was reset with.
        Assert.Equal(48000u, encoder.SampleRate);
    }

    // ---- region of interest ----

    [Fact]
    public void RoiIsRefusedOnAnEncoderWithoutTheCapability()
    {
        using var session = StartSession();

        // x264 advertises ROI (OBS_ENCODER_CAP_ROI) but the VAAPI H.264 encoder does not.
        using var vaapi = ObsEncoder.CreateVideo(VaapiId, "no roi");
        Assert.False(vaapi.Caps.HasFlag(ObsEncoderCaps.Roi));
        Assert.False(vaapi.AddRoi(ObsEncoderRoi.Square(0, 0, 100, 0.5f)));
        Assert.False(vaapi.HasRoi);
    }

    [Fact]
    public void RoiIsAcceptedOnAnEncoderWithTheCapability()
    {
        using var session = StartSession();

        using var x264 = ObsEncoder.CreateVideo(X264Id, "roi");
        Assert.True(x264.Caps.HasFlag(ObsEncoderCaps.Roi));

        // The add is accepted for an encoder with the capability, and refused for one without.
        Assert.True(x264.AddRoi(ObsEncoderRoi.Square(320, 180, 100, 0.5f)));

        // Whether the region is reported as present only becomes meaningful once the encoder is
        // started — measured: add returns true while has_roi still reports false until then. What
        // is asserted here is the acceptance contract, which is what the capability gates.
        x264.ClearRoi();
    }
}
