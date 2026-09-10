// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Obs.IntegrationTests;

public sealed class ObsOutputTests
{
    private const string FfmpegMuxerId = "ffmpeg_muxer";
    private const string FfmpegOutputId = "ffmpeg_output";
    private const string HlsMuxerId = "ffmpeg_hls_muxer";
    private const string MpegtsMuxerId = "ffmpeg_mpegts_muxer";
    private const string ReplayBufferId = "replay_buffer";

    [SkippableFact]
    public void TheOutputTypesThisMachineHas_AreRegistered()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var ids = ObsOutput.EnumerateTypeIds();

        Assert.Contains(FfmpegMuxerId, ids);
        Assert.Contains(FfmpegOutputId, ids);
        Assert.Contains(HlsMuxerId, ids);
        Assert.Contains(MpegtsMuxerId, ids);
        Assert.Contains(ReplayBufferId, ids);

        Assert.NotNull(ObsOutput.GetTypeDisplayName(FfmpegMuxerId));
    }

    [SkippableFact]
    public void TheAvailabilityProbe_IsThatTheDisplayNameIsNotNull()
    {
        using var session = ObsSession.StartWithSourceTypes();

        Assert.False(ObsOutput.IsTypeRegistered("tript_no_such_output"));
        Assert.Null(ObsOutput.GetTypeDisplayName("tript_no_such_output"));
        Assert.False(ObsOutput.IsTypeRegistered("flv_output"));
        Assert.False(ObsOutput.IsTypeRegistered("rtmp_output"));
    }

    [SkippableFact]
    public void TheUnavailableIds_AreMissingFromTheEnumeration()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var ids = ObsOutput.EnumerateTypeIds();

        Assert.DoesNotContain("tript_no_such_output", ids);
        Assert.DoesNotContain("flv_output", ids);
        Assert.DoesNotContain("rtmp_output", ids);
    }

    [SkippableFact]
    public void CreatedWithTheRegisteredId_TheOutputReportsItBack()
    {
        using var session = ObsSession.StartWithSourceTypes();

        using var output = ObsOutput.Create(FfmpegMuxerId, "an output");

        Assert.Equal(FfmpegMuxerId, output.Id);
        Assert.Equal("an output", output.Name);
    }

    [SkippableFact]
    public void AnUnregisteredOutputId_IsRefusedRatherThanGivenAPlaceholder()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var failure = Assert.Throws<ObsException>(() => ObsOutput.Create("tript_no_such_output", "ghost"));

        Assert.Contains("tript_no_such_output", failure.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void TheInstanceFlags_MatchTheTypeFlags()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var fromType = ObsOutput.GetTypeFlags(FfmpegMuxerId);
        using var output = ObsOutput.Create(FfmpegMuxerId, "flags");

        Assert.Equal(fromType, output.Flags);
        Assert.NotEqual(ObsOutputFlags.None, output.Flags);
    }

    [SkippableFact]
    public void TheFileMuxer_IsAnEncodedAvOutputThatDoesNotNeedAService()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var flags = ObsOutput.GetTypeFlags(FfmpegMuxerId);

        Assert.True(flags.HasFlag(ObsOutputFlags.Video));
        Assert.True(flags.HasFlag(ObsOutputFlags.Audio));
        Assert.True(flags.HasFlag(ObsOutputFlags.Encoded));
        Assert.False(flags.HasFlag(ObsOutputFlags.Service));
        Assert.True(flags.HasFlag(ObsOutputFlags.MultiTrack));
    }

    [SkippableFact]
    public void TheStreamingMuxers_AreTheServiceShapedOutputs()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var mpegts = ObsOutput.GetTypeFlags(MpegtsMuxerId);
        Assert.True(mpegts.HasFlag(ObsOutputFlags.Encoded));
        Assert.True(mpegts.HasFlag(ObsOutputFlags.Service));

        var hls = ObsOutput.GetTypeFlags(HlsMuxerId);
        Assert.True(hls.HasFlag(ObsOutputFlags.Encoded));
        Assert.True(hls.HasFlag(ObsOutputFlags.Service));

        var generic = ObsOutput.GetTypeFlags(FfmpegOutputId);
        Assert.False(generic.HasFlag(ObsOutputFlags.Encoded));
        Assert.False(generic.HasFlag(ObsOutputFlags.Service));
    }

    [SkippableFact]
    public void TheFileMuxer_DeclaresOnlyThePathProperty()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var properties = ObsOutput.EnumerateTypeProperties(FfmpegMuxerId);

        var path = Assert.Single(properties);
        Assert.Equal("path", path.Name);
        Assert.Equal(ObsPropertyType.Text, path.Type);
    }

    [SkippableFact]
    public void TheFileMuxer_DefaultsAreEmpty()
    {
        using var session = ObsSession.StartWithSourceTypes();

        using var defaults = ObsOutput.GetTypeDefaults(FfmpegMuxerId);

        Assert.NotNull(defaults);
        Assert.Empty(defaults.EnumerateEntries());
    }

    [SkippableFact]
    public void AnAudioEncoder_AssignedToASlot_ComesBackFromThatSlot()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var output = ObsOutput.Create(FfmpegMuxerId, "wiring");

        using var audio = ObsEncoder.CreateAudio("ffmpeg_aac", "wiring audio");
        output.SetAudioEncoder(audio, 0);

        using var readBack = output.GetAudioEncoder(0);

        Assert.NotNull(readBack);
        Assert.Equal("ffmpeg_aac", readBack!.Id);
        Assert.Equal("wiring audio", readBack.Name);

        Assert.Null(output.GetAudioEncoder(1));
    }

    [SkippableFact]
    public void ReadingAnEncoderSlot_DoesNotConsumeTheOutputsOwnReference()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var output = ObsOutput.Create(FfmpegMuxerId, "slot refcount");
        using var audio = ObsEncoder.CreateAudio("ffmpeg_aac", "slot refcount audio");
        output.SetAudioEncoder(audio, 0);

        for (var read = 0; read < 4; read++)
        {
            using var readBack = output.GetAudioEncoder(0);
            Assert.Equal("slot refcount audio", readBack!.Name);
        }

        using var stillThere = output.GetAudioEncoder(0);
        Assert.Equal("slot refcount audio", stillThere!.Name);
    }

    [SkippableFact]
    public void AWrittenSettingsKey_IsReadBackThroughTheOutput()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var settings = new ObsSettings();
        settings.SetString("path", "/tmp/nonexistent-directory/out.mp4");
        using var output = ObsOutput.Create(FfmpegMuxerId, "settings", settings);

        using var readBack = output.GetSettings();

        Assert.Equal("/tmp/nonexistent-directory/out.mp4", readBack.GetString("path"));
    }

    [SkippableFact]
    public void ANewOutput_IsNotActiveAndNotPaused()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var output = ObsOutput.Create(FfmpegMuxerId, "fresh");

        Assert.False(output.IsActive);
        Assert.False(output.IsPaused);
    }

    [SkippableFact]
    public void AFileOutput_ReportsNoNetworkVocabulary()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var output = ObsOutput.Create(FfmpegMuxerId, "stats");

        Assert.False(output.IsReconnecting);
        Assert.Equal(0f, output.Congestion);
        Assert.Equal(-1, output.ConnectTimeMilliseconds);
    }

    [SkippableFact]
    public void ABadPath_MakesStartRefuseAndNameTheReason()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var output = WithEncodersWired(session, ObsOutput.Create(FfmpegMuxerId, "bad path",
            WithPath(new ObsSettings(), Path.Combine(Path.GetTempPath(), $"no-such-tript-dir-{Guid.NewGuid():N}", "out.mp4"))));

        Assert.False(output.Start());
        Assert.NotNull(output.LastError);
        Assert.NotEqual(string.Empty, output.LastError);
    }

    [SkippableFact]
    public void StartingWithoutEncoders_MakesStartRefuse()
    {
        using var session = ObsSession.StartWithSourceTypes();

        using var output = ObsOutput.Create(FfmpegMuxerId, "no encoders");

        Assert.False(output.Start());
        Assert.Null(output.LastError);
    }

    [SkippableFact]
    public void AFailedStart_DoesNotEmitAStopSignal()
    {
        using var session = ObsSession.StartWithSourceTypes();

        using var settings = new ObsSettings();
        settings.SetString("path", "/tmp/nonexistent-directory/out.mp4");
        using var output = ObsOutput.Create(FfmpegMuxerId, "never started", settings);

        var stops = new List<ObsOutputStopEvent>();
        output.Stopped += (_, payload) => stops.Add(payload);

        Assert.False(output.Start());
        Thread.Sleep(300);

        Assert.Empty(stops);
    }

    private static ObsOutput WithEncodersWired(ObsSession session, ObsOutput output)
    {
        session.Runtime.TryGetVideoHandle(out var video);
        session.Runtime.TryGetAudioHandle(out var audio);

        using var videoSettings = new ObsSettings();
        videoSettings.SetString("rate_control", "CRF");
        videoSettings.SetInt("crf", 22);
        videoSettings.SetInt("keyint_sec", 1);
        var videoEncoder = ObsEncoder.CreateVideo("obs_x264", $"{output.Name} video", videoSettings);
        videoEncoder.BindToVideo(video);

        using var audioSettings = new ObsSettings();
        audioSettings.SetInt("bitrate", 128);
        var audioEncoder = ObsEncoder.CreateAudio("ffmpeg_aac", $"{output.Name} audio", audioSettings, mixerIndex: 0);
        audioEncoder.BindToAudio(audio);

        output.SetVideoEncoder(videoEncoder);
        output.SetAudioEncoder(audioEncoder, 0);

        _ = videoEncoder;
        _ = audioEncoder;
        return output;
    }

    private static ObsSettings WithPath(ObsSettings settings, string path)
    {
        settings.SetString("path", path);
        return settings;
    }
}
