// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Obs.IntegrationTests;

public sealed class ObsEncoderTests
{
    private const string X264Id = "obs_x264";
    private const string AacId = "ffmpeg_aac";
    private const string VaapiId = "ffmpeg_vaapi";
    private const string Av1VaapiId = "av1_ffmpeg_vaapi";

    private static bool MachineHasARenderNode() =>
        Directory.Exists("/dev/dri") && Directory.EnumerateFileSystemEntries("/dev/dri", "renderD*").Any();

    private static void RequireVaapiHardware() =>
        Skip.IfNot(MachineHasARenderNode(), "This machine has no GPU render node, so libobs offers no VAAPI encoder.");

    private static ObsSession StartSession()
    {
        var session = ObsSession.StartWithSourceTypes();
        return session;
    }

    [SkippableFact]
    public void TheEncodersThisMachineExposes_AreRegistered()
    {
        using var session = StartSession();

        var ids = ObsEncoder.EnumerateTypeIds();

        Assert.Contains(X264Id, ids);
        Assert.Contains(AacId, ids);

        Assert.Equal("h264", ObsEncoder.GetTypeCodec(X264Id));
        Assert.Equal("aac", ObsEncoder.GetTypeCodec(AacId));
    }

    [SkippableFact]
    public void AMachineWithAGpuRenderNode_ExposesTheVaapiEncoders()
    {
        RequireVaapiHardware();
        using var session = StartSession();

        var ids = ObsEncoder.EnumerateTypeIds();

        Assert.Contains(VaapiId, ids);
        Assert.Contains(Av1VaapiId, ids);
    }

    [SkippableFact]
    public void TheAvailabilityProbe_IsThatTheCodecIsNotNull()
    {
        using var session = StartSession();

        Assert.False(ObsEncoder.IsTypeRegistered("tript_no_such_encoder"));
        Assert.Null(ObsEncoder.GetTypeCodec("tript_no_such_encoder"));
        Assert.False(ObsEncoder.IsTypeRegistered("obs_nvenc_h264_tex"));
        Assert.False(ObsEncoder.IsTypeRegistered("obs_qsv11_v2"));

        Assert.Equal(ObsEncoderType.Audio, ObsEncoder.GetType("tript_no_such_encoder"));
    }

    [SkippableFact]
    public void TheUnavailableIds_AreMissingFromTheEnumeration()
    {
        using var session = StartSession();

        var ids = ObsEncoder.EnumerateTypeIds();

        Assert.DoesNotContain("obs_nvenc_h264_tex", ids);
        Assert.DoesNotContain("obs_qsv11_v2", ids);
    }

    [SkippableFact]
    public void TheUnavailablePath_ReportsNoDefaultsAndNoProperties()
    {
        using var session = StartSession();

        Assert.Null(ObsEncoder.GetTypeDefaults("obs_nvenc_h264_tex"));
        Assert.Null(ObsEncoder.GetTypeDefaults("obs_qsv11_v2"));
        Assert.Empty(ObsEncoder.EnumerateTypeProperties("obs_nvenc_h264_tex"));
        Assert.Empty(ObsEncoder.EnumerateTypeProperties("obs_qsv11_v2"));
    }

    [SkippableFact]
    public void CreatingAnUnregisteredEncoder_IsRefusedRatherThanGivenAPlaceholder()
    {
        using var session = StartSession();

        var videoFailure = Assert.Throws<ObsException>(() => ObsEncoder.CreateVideo("tript_no_such_encoder", "ghost"));
        Assert.Contains("tript_no_such_encoder", videoFailure.Message, StringComparison.Ordinal);

        var audioFailure = Assert.Throws<ObsException>(() => ObsEncoder.CreateAudio("tript_no_such_encoder", "ghost-aac"));
        Assert.Contains("tript_no_such_encoder", audioFailure.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void ACreatedVideoEncoder_ReportsTheTypeIdNameCodecAndType()
    {
        using var session = StartSession();
        using var encoder = ObsEncoder.CreateVideo(X264Id, "video encoder");

        Assert.Equal(X264Id, encoder.Id);
        Assert.Equal("video encoder", encoder.Name);
        Assert.Equal("h264", encoder.Codec);
        Assert.Equal(ObsEncoderType.Video, encoder.Type);
    }

    [SkippableFact]
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

    [SkippableFact]
    public void AnEncodersName_CanBeChangedAfterCreation()
    {
        using var session = StartSession();
        using var encoder = ObsEncoder.CreateVideo(X264Id, "first name");

        encoder.Name = "second name";

        Assert.Equal("second name", encoder.Name);
    }

    [SkippableFact]
    public void ANewEncoder_IsNeitherActiveNorFailed()
    {
        using var session = StartSession();
        using var encoder = ObsEncoder.CreateAudio(AacId, "idle");

        Assert.False(encoder.IsActive);
        Assert.Null(encoder.LastError);
    }

    [SkippableFact]
    public void AnEmptyOrNullTypeId_IsRejectedBeforeItReachesLibobs()
    {
        using var session = StartSession();

        Assert.Throws<ArgumentNullException>(() => ObsEncoder.CreateVideo(null!, "named"));
        Assert.Throws<ArgumentException>(() => ObsEncoder.CreateVideo(string.Empty, "named"));
        Assert.Throws<ArgumentNullException>(() => ObsEncoder.CreateVideo(X264Id, null!));
        Assert.Throws<ArgumentException>(() => ObsEncoder.CreateVideo(X264Id, string.Empty));
    }

    [SkippableFact]
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

        settings.SetInt("crf", 30);

        using var again = encoder.GetSettings();
        Assert.Equal(30, again.GetInt("crf"));
    }

    [SkippableFact]
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

        Assert.Equal("CRF", readBack.GetString("rate_control"));
    }

    [SkippableFact]
    public void TheAacTypeDefaults_CarryTheBitrateDefault()
    {
        using var session = StartSession();

        using var defaults = ObsEncoder.GetTypeDefaults(AacId);
        Assert.NotNull(defaults);

        Assert.Equal(128, defaults!.GetInt("bitrate"));
    }

    [SkippableFact]
    public void TheX264PropertyList_NamesExactlyTheKeysThePluginReads()
    {
        using var session = StartSession();

        var properties = ObsEncoder.EnumerateTypeProperties(X264Id);
        var names = properties.Select(property => property.Name).ToArray();

        Assert.Equal(
            ["rate_control", "bitrate", "use_bufsize", "buffer_size", "crf", "keyint_sec", "preset", "profile", "tune", "x264opts", "repeat_headers"],
            names);
    }

    [SkippableFact]
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

    [SkippableFact]
    public void TheVaapiFamily_OffersMaxrateAndQpRatherThanMaxBitrateAndCqp()
    {
        RequireVaapiHardware();
        using var session = StartSession();

        var properties = ObsEncoder.EnumerateTypeProperties(VaapiId);
        var names = properties.Select(property => property.Name).ToArray();

        Assert.Contains("vaapi_device", names);
        Assert.Contains("rate_control", names);
        Assert.Contains("maxrate", names);
        Assert.Contains("qp", names);
        Assert.Contains("keyint_sec", names);
        Assert.Contains("ffmpeg_opts", names);

        Assert.DoesNotContain("max_bitrate", names);
        Assert.DoesNotContain("cqp", names);
    }

    [SkippableFact]
    public void AVideoEncoderBeforeBinding_ReportsZeroDimensions()
    {
        using var session = StartSession();
        using var encoder = ObsEncoder.CreateVideo(X264Id, "unbound");

        Assert.Equal(0u, encoder.Width);
        Assert.Equal(0u, encoder.Height);
    }

    [SkippableFact]
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

    [SkippableFact]
    public void AnAudioEncoder_BeforeBindingReportsNoSampleRate()
    {
        using var session = StartSession();
        using var encoder = ObsEncoder.CreateAudio(AacId, "unbound audio");

        Assert.Equal(0u, encoder.SampleRate);
    }

    [SkippableFact]
    public void BindingToAudio_MakesTheSampleRateReportTheMixRate()
    {
        using var session = StartSession();
        using var encoder = ObsEncoder.CreateAudio(AacId, "bound audio");

        Assert.True(session.Runtime.TryGetAudioHandle(out var audio));
        encoder.BindToAudio(audio);

        Assert.Equal(48000u, encoder.SampleRate);
    }
}
