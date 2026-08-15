// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Obs.IntegrationTests;

// The outputs surface against the real library: the availability probes, creation and identity, the
// settings the muxer reads, wiring encoders to the output, state, statistics, and the three failure
// channels. Recording to disk is ObsOutputRecordingTests — this class ends where a file begins.
public sealed class ObsOutputTests
{
    private const string FfmpegMuxerId = "ffmpeg_muxer";
    private const string FfmpegOutputId = "ffmpeg_output";
    private const string HlsMuxerId = "ffmpeg_hls_muxer";
    private const string MpegtsMuxerId = "ffmpeg_mpegts_muxer";
    private const string ReplayBufferId = "replay_buffer";

    // ---- availability ----

    [Fact]
    public void TheOutputTypesThisMachineHas_AreRegistered()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var ids = ObsOutput.EnumerateTypeIds();

        // All from obs-ffmpeg, which is in the safe module list, so all must register. Measured:
        // the flv/rtmp streamers are NOT among them on this OBS build — they come from modules that
        // are not loaded headless — which is exactly the "available on other machines" case.
        Assert.Contains(FfmpegMuxerId, ids);
        Assert.Contains(FfmpegOutputId, ids);
        Assert.Contains(HlsMuxerId, ids);
        Assert.Contains(MpegtsMuxerId, ids);
        Assert.Contains(ReplayBufferId, ids);

        // The display name answers the same question directly, and agrees with the enumeration.
        Assert.NotNull(ObsOutput.GetTypeDisplayName(FfmpegMuxerId));
    }

    [Fact]
    public void TheAvailabilityProbe_IsThatTheDisplayNameIsNotNull()
    {
        using var session = ObsSession.StartWithSourceTypes();

        // Unregistered — measured on this box: no module registers these ids, which is the point.
        // obs_output_create would answer a *placeholder* for them rather than null — measured — so
        // a null-check on the create result proves nothing. The display-name probe is the reliable
        // one, exactly as the bindings for sources and encoders found.
        Assert.False(ObsOutput.IsTypeRegistered("tript_no_such_output"));
        Assert.Null(ObsOutput.GetTypeDisplayName("tript_no_such_output"));
        Assert.False(ObsOutput.IsTypeRegistered("flv_output"));
        Assert.False(ObsOutput.IsTypeRegistered("rtmp_output"));
    }

    [Fact]
    public void TheUnavailableIds_AreMissingFromTheEnumeration()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var ids = ObsOutput.EnumerateTypeIds();

        Assert.DoesNotContain("tript_no_such_output", ids);
        Assert.DoesNotContain("flv_output", ids);
        Assert.DoesNotContain("rtmp_output", ids);
    }

    // ---- creation ----

    [Fact]
    public void CreatedWithTheRegisteredId_TheOutputReportsItBack()
    {
        using var session = ObsSession.StartWithSourceTypes();

        using var output = ObsOutput.Create(FfmpegMuxerId, "an output");

        Assert.Equal(FfmpegMuxerId, output.Id);
        Assert.Equal("an output", output.Name);
    }

    [Fact]
    public void AnUnregisteredOutputId_IsRefusedRatherThanGivenAPlaceholder()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var failure = Assert.Throws<ObsException>(() => ObsOutput.Create("tript_no_such_output", "ghost"));

        Assert.Contains("tript_no_such_output", failure.Message, StringComparison.Ordinal);
    }

    // The instance reports the same flags its type declared, so the type probe is what a recorder
    // leans on before it has an instance.
    [Fact]
    public void TheInstanceFlags_MatchTheTypeFlags()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var fromType = ObsOutput.GetTypeFlags(FfmpegMuxerId);
        using var output = ObsOutput.Create(FfmpegMuxerId, "flags");

        Assert.Equal(fromType, output.Flags);
        Assert.NotEqual(ObsOutputFlags.None, output.Flags);
    }

    // The recorded muxer is a file output: it takes encoded packets and does not need a service.
    // The flags are the measured 0x37 — video, audio and encoded, plus multi-track for both.
    [Fact]
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

    // The other muxers on this machine are the shape of an output that does need a service: the
    // mpegts and HLS muxers report OBS_OUTPUT_SERVICE alongside ENCODED (measured flags 0x1f).
    // The generic ffmpeg_output does not — it is a *non-encoded* file output (measured 0x33).
    [Fact]
    public void TheStreamingMuxers_AreTheServiceShapedOutputs()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var mpegts = ObsOutput.GetTypeFlags(MpegtsMuxerId);
        Assert.True(mpegts.HasFlag(ObsOutputFlags.Encoded));
        Assert.True(mpegts.HasFlag(ObsOutputFlags.Service));

        var hls = ObsOutput.GetTypeFlags(HlsMuxerId);
        Assert.True(hls.HasFlag(ObsOutputFlags.Encoded));
        Assert.True(hls.HasFlag(ObsOutputFlags.Service));

        // And the generic ffmpeg_output is the non-encoded file-output shape, which is why an
        // encoded recorder must not confuse it with the muxer.
        var generic = ObsOutput.GetTypeFlags(FfmpegOutputId);
        Assert.False(generic.HasFlag(ObsOutputFlags.Encoded));
        Assert.False(generic.HasFlag(ObsOutputFlags.Service));
    }

    // ---- settings ----

    // The path property is the whole key surface the muxer reads. Measured: it is a plain TEXT
    // property on 32.2.1 (type 4), not a PATH picker — the plugin takes the path as a string and
    // only the frontend's own recording UI offers the browse button. A binding that asserted Path
    // here would be asserting what a picker should be, not what the plugin declares.
    [Fact]
    public void TheFileMuxer_DeclaresOnlyThePathProperty()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var properties = ObsOutput.EnumerateTypeProperties(FfmpegMuxerId);

        var path = Assert.Single(properties);
        Assert.Equal("path", path.Name);
        Assert.Equal(ObsPropertyType.Text, path.Type);
    }

    // The defaults object is empty — measured — even though the path property exists. The plugin
    // carries the path's default in the property itself, not in the defaults object, so a recorder
    // that starts from GetTypeDefaults gets a blank object. It is the property list that is the
    // contract, not the defaults.
    [Fact]
    public void TheFileMuxer_DefaultsAreEmpty()
    {
        using var session = ObsSession.StartWithSourceTypes();

        using var defaults = ObsOutput.GetTypeDefaults(FfmpegMuxerId);

        Assert.NotNull(defaults);
        Assert.Empty(defaults.EnumerateEntries());
    }

    // ---- wiring ----

    [Fact]
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

        // Slots that were never assigned report nothing.
        Assert.Null(output.GetAudioEncoder(1));
    }

    [Fact]
    public void AWrittenSettingsKey_IsReadBackThroughTheOutput()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var settings = new ObsSettings();
        settings.SetString("path", "/tmp/nonexistent-directory/out.mp4");
        using var output = ObsOutput.Create(FfmpegMuxerId, "settings", settings);

        using var readBack = output.GetSettings();

        Assert.Equal("/tmp/nonexistent-directory/out.mp4", readBack.GetString("path"));
    }

    // ---- state ----

    [Fact]
    public void ANewOutput_IsNotActiveAndNotPaused()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var output = ObsOutput.Create(FfmpegMuxerId, "fresh");

        Assert.False(output.IsActive);
        Assert.False(output.IsPaused);
    }

    // The muxer reports no reconnection vocabulary, which is the shape of a file output rather than
    // a streamer. The connect-time field reports -1 (not 0) on a fresh, never-started output —
    // measured — so it is -1 that means "no connection has ever been attempted", and the test pins
    // that rather than assuming 0.
    [Fact]
    public void AFileOutput_ReportsNoNetworkVocabulary()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var output = ObsOutput.Create(FfmpegMuxerId, "stats");

        Assert.False(output.IsReconnecting);
        Assert.Equal(0f, output.Congestion);
        Assert.Equal(-1, output.ConnectTimeMilliseconds);
    }

    // ---- failure channels ----

    // The synchronous channel, provoked with the output shaped the way a recorder shapes it: a bad
    // path makes start refuse and names the reason. Measured: last_error is only set once encoders
    // are wired — without them the refusal says "no media" and names nothing, so this is the shape
    // that proves the reason is surfaced at all. The bad directory is unique so a stale one from an
    // earlier run cannot satisfy it.
    [Fact]
    public void ABadPath_MakesStartRefuseAndNameTheReason()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var output = WithEncodersWired(session, ObsOutput.Create(FfmpegMuxerId, "bad path",
            WithPath(new ObsSettings(), Path.Combine(Path.GetTempPath(), $"no-such-tript-dir-{Guid.NewGuid():N}", "out.mp4"))));

        Assert.False(output.Start());
        Assert.NotNull(output.LastError);
        Assert.NotEqual(string.Empty, output.LastError);
    }

    // The missing-encoder variant: start refuses without ever naming a reason, which is the second
    // measured shape of the synchronous channel — the "no media" refusal names nothing.
    [Fact]
    public void StartingWithoutEncoders_MakesStartRefuse()
    {
        using var session = ObsSession.StartWithSourceTypes();

        using var output = ObsOutput.Create(FfmpegMuxerId, "no encoders");

        Assert.False(output.Start());
        Assert.Null(output.LastError);
    }

    // The asynchronous channel, provoked: with a valid path but no encoders, start still refuses,
    // and the refusal is synchronous — the stop signal must not fire, because nothing started.
    [Fact]
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

    // ---- test helpers ----

    // The shape a recorder actually starts: a muxer output with a video and an audio encoder wired,
    // bound to the session's mixes. Without this, a bad path refuses with "no media" and names
    // nothing — measured — so the tests that assert on the named reason wire encoders first.
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

        // The encoders belong to the output for the duration of this test; the caller's references
        // are kept alive by the output's own internal references, so disposal here only drops the
        // wrapper.
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
