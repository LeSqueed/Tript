// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;
using Tript.Settings;

namespace Tript.Recorder;

// The concrete recorder session over the OBS binding: an existing runtime plus the sources the app
// provides. What goes on the recording channel is a private scene the session composes — the
// colour source as a canvas-sized background and, when a game-capture source can be created, that
// source composited above it — rather than the raw colour source, whose own size is the plugin's
// default block. Building a session output is exactly the Layer 5 wiring — an ffmpeg_muxer output,
// a video encoder scaled to the resolved resolution, an audio encoder on mixer zero, both bound to
// the session's mixes. The output is the recorder's to own; the sources are borrowed.
public sealed class ObsRecorderSession : IRecorderSession
{

    private const string FfmpegMuxerId = "ffmpeg_muxer";
    private const string X264Id = "obs_x264";

    // The source type id the platform's game-capture plugin registers. The settings keys are
    // discovered, not this literal; the id is the one ObsCaptureSource probes for.
    private const string GameCaptureId = "game_capture";

    // The settings model's encoder default ("x264", SettingPages.cs). It is a placeholder meaning
    // "the backend decides" — the real software id is obs_x264 — so it is never treated as an
    // explicit user choice when the effective encoder is resolved.
    private const string X264DefaultId = "x264";

    private const string FfmpegAacId = "ffmpeg_aac";
    private const uint VideoChannel = 0;

    // The keyframe interval, in seconds, written as keyint_sec. A player can only seek to a
    // keyframe, so this is the seek granularity of every recording and the boundary the clip
    // engine can cut on without re-encoding. One second is the interval every family accepts
    // (the x264 table's keyint_sec range covers it) and is short enough that a bookmark lands
    // where the user set it.
    private const int KeyframeIntervalSeconds = 1;

    private readonly ObsSource _source;
    private readonly ObsScene _scene;
    private readonly ObsSource? _gameCaptureSource;

    // The scene is the session's own composition: the colour source is its background and, when a
    // game-capture source was created, that source is above it. A game-capture target may be passed
    // up front (a game already detected at session construction) and retargeted later without
    // restarting the source via RetargetGame.
    public ObsRecorderSession(ObsRuntime runtime, ObsSource source, ObsGameCaptureTarget? gameCaptureTarget = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(source);

        Runtime = runtime;
        _source = source.AddReference();

        // The scene's items each take their own reference to the source they place (obs_scene_add),
        // so the items keep both sources alive for as long as the scene is.
        _scene = ObsScene.CreatePrivate("recorder scene");
        try
        {
            if (_scene.AddSource(_source) is null)
                throw new ObsException("The recorder scene refused the colour source.");

            // The game-capture source is created whenever the platform has one, even with no
            // initial target: RetargetGame re-points it at the detected game without restarting it,
            // so a game detected after session construction still lands in the recording.
            if (CreateGameCaptureSource(gameCaptureTarget, _scene) is { } capture)
                _gameCaptureSource = capture;
        }
        catch
        {
            // Nothing was handed out: the scene and the session's reference are still both ours.
            _scene.Dispose();
            _source.Dispose();
            throw;
        }
    }

    public ObsRuntime Runtime { get; }

    public IRecorderOutput CreateOutput(ResolvedRecorderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!ObsOutput.IsTypeRegistered(FfmpegMuxerId))
            throw new ObsException($"No loaded module registers the output type '{FfmpegMuxerId}'.");

        if (!Runtime.TryGetVideoHandle(out var video))
            throw new ObsException("The runtime has no video mix; obs_reset_video must succeed before recording.");

        if (!Runtime.TryGetAudioHandle(out var audio))
            throw new ObsException("The runtime has no audio mix; obs_reset_audio must succeed before recording.");

        // The colour source is the background; it must fill the canvas rather than render as the
        // plugin's default block. Sizing happens here, where the resolved canvas is known.
        SizeColourSourceToCanvas(settings.ResolutionWidth, settings.ResolutionHeight);

        var videoEncoderId = ResolveVideoEncoderId(settings.Encoder);
        if (videoEncoderId is null)
            throw new ObsException("No loaded module registers a usable H.264 video encoder.");

        if (!ObsEncoder.IsTypeRegistered(FfmpegAacId))
            throw new ObsException($"No loaded module registers the audio encoder '{FfmpegAacId}'.");

        var outputSettings = new ObsSettings();
        outputSettings.SetString("path", settings.OutputPath);

        var output = ObsOutput.Create(FfmpegMuxerId, "recorder output", outputSettings);

        // The encoders outlive this method by being handed to MuxerOutput, and that is not tidiness.
        // The settings objects below are safe to dispose early because libobs takes a reference of
        // its own; the encoders have no such guarantee from this side. obs_output_set_video_encoder
        // records the encoder pointer on the output, while ObsEncoderHandle holds the only managed
        // reference and releases it — obs_encoder_release — from its finalizer. Encoders left as
        // locals are unreachable the moment this method returns, so a GC at any point afterwards can
        // drop the refcount while the output is still pointing at them, and the output would then
        // initialize a released encoder at obs_output_start. Ownership belongs with the output
        // wrapper, which is the object whose lifetime the recorder actually controls.
        ObsEncoder? videoEncoder = null;
        ObsEncoder? audioEncoder = null;
        AudioRouting? audioRouting = null;

        try
        {
            // The video encoder carries the resolved resolution and frame rate; libobs scales the mix
            // to the requested size. The encoder id resolved above decides which key set is written:
            // the user's rate-control choice, coerced to something the resolved family accepts, under
            // that family's own mode string and quantiser or bitrate keys (spec/obs-binding Part 10).
            var rateControl = ResolveRateControl(videoEncoderId, settings.RateControl);
            using (var videoSettings = new ObsSettings())
            {
                videoSettings.SetString("rate_control", rateControl.Mode);

                // Which of the two dials the mode actually reads is a per-family fact, not a
                // per-mode one: a constant-quality mode reads only the quantiser, CBR reads only the
                // bitrate, and x264's VBR reads *both* — its bitrate is a VBV cap over a CRF target,
                // and its crf key is documented as meaningful under VBR as well as CRF. Writing a key
                // the mode does not read is not an error, but it is not harmless either: x264 zeroes
                // crf under CBR and zeroes bitrate under CRF, so the settings object should say only
                // what the mode means.
                if (rateControl.QuantiserKey is { } quantiserKey)
                    videoSettings.SetInt(quantiserKey, MapQualityToQuantiser(settings.Quality));

                if (rateControl.BitrateKey is { } bitrateKey)
                    videoSettings.SetInt(bitrateKey, ClampBitrateKbps(settings.BitrateKbps));

                if (rateControl.MaxBitrateKey is { } maxBitrateKey)
                    videoSettings.SetInt(maxBitrateKey, ResolveMaxBitrateKbps(settings.BitrateKbps, settings.MaxBitrateKbps));

                // keyint_sec is an interval in *seconds*, and the encoder converts it to frames
                // itself using the mix's frame rate. Passing the frame rate here asked for a
                // 60-second GOP that then clamped to 10, which is why clips and bookmark seeks
                // landed on coarse boundaries: a player can only seek to a keyframe, so the GOP
                // length is the seek granularity. One second is the interval the comment always
                // claimed and the granularity the clip engine needs.
                videoSettings.SetInt("keyint_sec", KeyframeIntervalSeconds);

                videoEncoder = ObsEncoder.CreateVideo(videoEncoderId, "recorder video", videoSettings);
                videoEncoder.BindToVideo(video);
                videoEncoder.SetScaledSize((uint)settings.ResolutionWidth, (uint)settings.ResolutionHeight);
                output.SetVideoEncoder(videoEncoder);
            }

            // The recording's audio comes from the resolved track plan when any track is
            // configured: each track's sources become wasapi capture sources (carrying the selected
            // device id) routed into that track's mixer, with a per-track encoder bound to that
            // mixer and assigned to the matching output slot. An empty track list keeps the single
            // programme-mix encoder on slot 0 — the Layer 5 shape — so a recording with no tracks
            // configured still carries audio.
            if (settings.AudioTracks.Count > 0)
            {
                var sink = new ObsAudioRoutingSink(output, audio, audioEncoderId: FfmpegAacId, scene: _scene);
                audioRouting = new AudioRoutingService(sink).Wire(AudioRoutingPlanner.Plan(settings.AudioTracks));
            }
            else
            {
                using (var audioSettings = new ObsSettings())
                {
                    audioSettings.SetInt("bitrate", 160);
                    audioEncoder = ObsEncoder.CreateAudio(FfmpegAacId, "recorder audio", audioSettings, mixerIndex: 0);
                    audioEncoder.BindToAudio(audio);
                    output.SetAudioEncoder(audioEncoder, 0);
                }
            }

            return new MuxerOutput(output, videoEncoder, audioEncoder, audioRouting);
        }
        catch
        {
            // Nothing was handed over, so this method still owns all three. The output goes first,
            // for the same reason MuxerOutput.Dispose releases in that order: an output must never be
            // left alive pointing at a released encoder.
            output.Dispose();
            videoEncoder?.Dispose();
            audioEncoder?.Dispose();
            audioRouting?.Dispose();
            throw;
        }
    }

    // The scene is what the recording renders, placed on the channel like any source (a scene *is*
    // a source); the channel takes a reference of its own.
    public void PlaceSourceOnChannel() => Runtime.SetOutputSource(VideoChannel, _scene);

    public void ClearSourceFromChannel() => Runtime.SetOutputSource(VideoChannel, (ObsSource?)null);

    // The seam AppHost calls when a game is detected at start time: re-points the live game-capture
    // source at the new target without restarting it (obs_source_update merges, so the capture_mode
    // the source was created with survives). False when there is no game-capture source — the
    // initial target was null, or the platform has no game capture at all.
    public bool RetargetGame(ObsGameCaptureTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return _gameCaptureSource is not null && ObsCaptureSource.Retarget(_gameCaptureSource, target);
    }

    // The scene goes first: releasing it destroys its items, and each item releases the reference
    // it took from its source. Only then do the session's own references drop the colour and
    // game-capture sources for good. All three Disposes are idempotent — each handle short-circuits
    // once closed — so a second Dispose here is harmless.
    public void Dispose()
    {
        _scene.Dispose();
        _source.Dispose();
        _gameCaptureSource?.Dispose();
    }

    // ---- scene composition ----

    // Creates and places the game-capture source for the initial target)Skip. Null when the
    // platform has no game-capture source (Linux), which the caller treats as "background only" —
    // exactly the scene shape before this work. A null target still creates the source (with the
    // capture_mode forced to window so an exe-only target is meaningful later) so RetargetGame has
    // something to re-point.
    private static ObsSource? CreateGameCaptureSource(ObsGameCaptureTarget? target, ObsScene scene)
    {
        if (ObsSourceProperties.EnumerateTypeProperties(GameCaptureId).Count == 0)
            return null;

        using var settings = target is not null
            ? ObsCaptureSource.BuildGameCaptureSettings(target) ?? new ObsSettings()
            : new ObsSettings();

        ApplyWindowCaptureMode(settings);

        var source = ObsSource.CreatePrivate(GameCaptureId, "app capture", settings);
        try
        {
            if (scene.AddSource(source) is null)
                throw new ObsException("The recorder scene refused the game-capture source.");
        }
        catch
        {
            source.Dispose();
            throw;
        }

        return source;
    }

    // The win-capture plugin's default capture_mode is "any_fullscreen": it hooks whichever
    // fullscreen window is in the foreground and ignores the exe key entirely (game-capture.c,
    // CAPTURE_MODE_ANY -> get_fullscreen_window). An exe-only target is only meaningful in the
    // window mode, where the window is matched by title/class/exe (game-capture.c,
    // get_selected_window -> ms_find_window; the default priority is WINDOW_PRIORITY_EXE). The mode
    // is a plugin-side value, so it is found through the source's own properties like the keys
    // ObsCaptureSource discovers — the mode list is the one whose items include the "any" mode, and
    // the window mode is the item that makes the exe key meaningful. Nothing here is hardcoded.
    private static void ApplyWindowCaptureMode(ObsSettings settings)
    {
        foreach (var property in ObsSourceProperties.EnumerateTypeProperties(GameCaptureId))
        {
            if (property.Type != ObsPropertyType.List)
                continue;

            if (!property.Items.Any(item =>
                    item.Format == ObsComboFormat.String &&
                    item.Value is string itemValue &&
                    itemValue.Contains("any", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var windowMode = property.Items.FirstOrDefault(item =>
                item.Format == ObsComboFormat.String &&
                item.Value is string itemValue &&
                itemValue.Contains("window", StringComparison.OrdinalIgnoreCase));

            if (windowMode.Value is string modeValue)
                settings.SetString(property.Name, modeValue);

            return;
        }
    }

    // The colour source is the recording's background. Placed straight on a channel it renders at
    // its own configured size — with no width/height setting, that is the plugin's default block,
    // which is what the "black screen with a white block" recordings this fixes showed. Sizing it
    // to the canvas once, through Update (the colour source's reconfiguration seam — GetSettings
    // hands back the source's live settings object), makes it fill the recording whatever
    // composites above it; game capture never changes the background's size.
    private void SizeColourSourceToCanvas(int width, int height)
    {
        using var settings = _source.GetSettings();
        if (settings.GetInt("width") >= width && settings.GetInt("height") >= height)
            return;

        settings.SetInt("width", width);
        settings.SetInt("height", height);
        _source.Update(settings);
    }

    // Which H.264 video encoder the runtime actually registered. The ids differ by machine and
    // runtime — obs_x264 on a software-only install, ffmpeg_vaapi on this machine, the texture-NVENC
    // ids where that plugin is present. Availability is structural, so whatever is registered is
    // something the machine can actually use: the plugins probe their hardware and register nothing
    // when it is absent.
    //
    // The settings value participates: a user who explicitly chose an encoder from the available
    // set is honoured when that id is still registered, and a configured software default ("x264") is
    // never treated as an explicit choice — it is the model's placeholder for "let the backend
    // decide", so the fallback still applies. An id that has since stopped being registered — a
    // pulled plugin, a swapped GPU — is not an explicit choice either; it falls through to the same
    // fallback rather than failing the recording.
    //
    // The fallback prefers hardware: a registered hardware id first, obs_x264 only when none is,
    // because a hardware encoder is what keeps a 1080p60 capture off the CPU. That is only safe
    // because the video settings are written per family (ResolveRateControlKeys) — a hardware encoder
    // handed x264's key set does not merely ignore it, it crashes. Null when the runtime registered
    // no H.264 video encoder at all, which CreateOutput reports as a wiring failure.
    internal static string? ResolveVideoEncoderId(string? configuredEncoder)
    {
        if (IsUsableId(configuredEncoder))
            return configuredEncoder;

        var usable = EnumerateUsableEncoderIds();
        return usable.FirstOrDefault(id => !string.Equals(id, X264Id, StringComparison.Ordinal))
               ?? usable.FirstOrDefault();
    }

    // The rate-control mode and the constant-quality key the resolved encoder's family reads, keyed
    // by id rather than by runtime version — from OBS 31 several families' key sets are live at once,
    // so "which id did we create" is the only answerable question (spec/obs-binding Part 10).
    //
    // None of these keys is in libobs; they are plugin-private literals, and the usual failure is
    // silent — a key the plugin does not read leaves it on its own default, producing a plausible
    // file at the wrong quality. VAAPI is not silent. obs-ffmpeg matches rate_control against a
    // NULL-terminated table with astrcmpi and, on no match, dereferences the terminator in strcmp:
    // writing x264's "CRF" to ffmpeg_vaapi segfaults inside obs_output_initialize_encoders at
    // obs_output_start. Measured, from a core dump — which is why this mapping exists at all.
    //
    // The quality number itself carries across: H.264 CRF and H.264 QP/CQP share the 0..51 scale, so
    // MapQualityToQuantiser's value is written unchanged and only the key name and the mode differ.
    // VAAPI names the quantiser "qp"; NVENC (both key sets), AMF and QSV all name it "cqp".
    //
    // This is the *constant-quality* answer specifically. The user-selectable modes go through
    // ResolveRateControl, which uses this pair for the quantiser modes and is where CBR and VBR are
    // resolved; this overload stays because constant quality is the fallback every coercion lands on.
    internal static (string RateControl, string QualityKey) ResolveRateControlKeys(string encoderId)
    {
        ArgumentException.ThrowIfNullOrEmpty(encoderId);

        var family = ClassifyFamily(encoderId);
        return (ConstantQualityModeString(family), QuantiserKey(family));
    }

    // Which rate-control modes the resolved encoder's family accepts, in the order the settings UI
    // should offer them. Derived from each family's accepted rate_control values (spec/obs-binding
    // Part 10, transcribed in tests/Tript.Obs.IntegrationTests/EncoderSettingsKeyTable.cs), because a
    // mode outside that list is the crash documented on ResolveRateControlKeys rather than a setting
    // the plugin ignores.
    //
    // Three asymmetries in this table are the whole reason it is a table:
    //
    //  * CRF exists only on x264. The hardware families spell constant quality "CQP", and x264 has no
    //    CQP mode at all — so the two constant-quality modes are not interchangeable spellings on the
    //    wire even though they mean the same thing to the user.
    //  * VAAPI is withheld from VBR. The specification has no VAAPI table, so the only VAAPI facts we
    //    hold are the ones measured here: it accepts CQP (what we have always written) and CBR (the
    //    string obs-ffmpeg's own table starts with, from the crash trace). Its VBR ceiling key is not
    //    among them, and a mistyped ceiling key fails silently at the wrong bitrate, so VBR is not
    //    offered for VAAPI rather than guessed at.
    //  * An id no table describes gets constant quality plus CBR. CBR is the one mode every documented
    //    family accepts and most of them default to, and CQP is the answer ResolveRateControlKeys has
    //    always given an unknown id. Anything beyond those two would be a guess about a plugin nobody
    //    here has seen.
    internal static IReadOnlyList<RateControlMode> SupportedRateControlModes(string encoderId)
    {
        ArgumentException.ThrowIfNullOrEmpty(encoderId);

        return ClassifyFamily(encoderId) switch
        {
            EncoderFamily.X264 => [RateControlMode.Crf, RateControlMode.Cbr, RateControlMode.Vbr],
            EncoderFamily.Vaapi => [RateControlMode.Cqp, RateControlMode.Cbr],
            EncoderFamily.Nvenc or EncoderFamily.Amf or EncoderFamily.Qsv =>
                [RateControlMode.Cqp, RateControlMode.Cbr, RateControlMode.Vbr],
            _ => [RateControlMode.Cqp, RateControlMode.Cbr]
        };
    }

    // The mode actually written for a requested one. This is the safety property that lets a settings
    // file travel: a config written on a software-only machine carries Crf, and the same file on an
    // NVIDIA machine resolves an NVENC id whose family rejects "CRF" — the mode the *user* chose is a
    // request, and the encoder family has the final say.
    //
    // An unsupported request falls back to the family's own constant-quality mode rather than to
    // something arbitrary, because that is the mode with no configuration of its own to get wrong: it
    // needs only the quality profile, which every mode's settings carry anyway. The two constant-quality
    // modes therefore also map into each other — Cqp on x264 becomes CRF, Crf on hardware becomes CQP —
    // so the intent ("constant quality") survives the coercion even though the spelling cannot.
    internal static RateControlMode CoerceRateControlMode(string encoderId, RateControlMode requested)
    {
        ArgumentException.ThrowIfNullOrEmpty(encoderId);

        return SupportedRateControlModes(encoderId).Contains(requested)
            ? requested
            : ConstantQualityMode(ClassifyFamily(encoderId));
    }

    // The mode string and the keys to write for a requested mode on a given encoder id: the single
    // answer CreateOutput needs. A null key means "this mode does not read that dial on this family",
    // which is not the same as zero — see the note at the write site.
    internal static EncoderRateControl ResolveRateControl(string encoderId, RateControlMode requested)
    {
        ArgumentException.ThrowIfNullOrEmpty(encoderId);

        var family = ClassifyFamily(encoderId);
        var mode = CoerceRateControlMode(encoderId, requested);

        return mode switch
        {
            RateControlMode.Crf or RateControlMode.Cqp =>
                new EncoderRateControl(ConstantQualityModeString(family), QuantiserKey(family), null, null),

            // CBR needs no ceiling: max_bitrate is documented as a VBR-only key on both families that
            // have one, and AMF has none at all (spec/obs-binding Part 10, cross-family trap 4).
            RateControlMode.Cbr => new EncoderRateControl("CBR", null, BitrateKey, null),

            // x264's VBR is a CRF target with a VBV cap, so it is the one family that reads the
            // quantiser and the bitrate together; its ceiling is the VBV pair (use_bufsize plus
            // buffer_size), not a max_bitrate key, and defaulting use_bufsize leaves the cap equal to
            // the bitrate — which is what a recording wants.
            RateControlMode.Vbr => new EncoderRateControl(
                "VBR",
                family == EncoderFamily.X264 ? QuantiserKey(family) : null,
                BitrateKey,
                MaxBitrateKey(family)),

            _ => throw new ArgumentOutOfRangeException(nameof(requested), requested, "Unknown rate-control mode.")
        };
    }

    // Which family an id belongs to. x264 is matched exactly and the rest by substring, because the
    // hardware families each ship several ids (jim_nvenc and obs_nvenc_h264_tex; ffmpeg_vaapi and its
    // texture variant) while "contains x264" would also catch a third-party id that merely mentions
    // it — and mistaking something else for x264 is the one error that writes "CRF" to a family that
    // segfaults on it. The substring matches ignore case: a plugin cases its own id, and an Ordinal
    // match would send FFMPEG_VAAPI down the unknown branch and write a quantiser key VAAPI never
    // reads.
    private static EncoderFamily ClassifyFamily(string encoderId)
    {
        if (string.Equals(encoderId, X264Id, StringComparison.Ordinal))
            return EncoderFamily.X264;

        if (encoderId.Contains("vaapi", StringComparison.OrdinalIgnoreCase))
            return EncoderFamily.Vaapi;

        if (encoderId.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
            return EncoderFamily.Nvenc;

        if (encoderId.Contains("amf", StringComparison.OrdinalIgnoreCase))
            return EncoderFamily.Amf;

        if (encoderId.Contains("qsv", StringComparison.OrdinalIgnoreCase))
            return EncoderFamily.Qsv;

        return EncoderFamily.Unknown;
    }

    // x264 is the only family whose constant-quality mode is CRF; everything else, including an id no
    // table describes, uses CQP — the one constant-quality mode every documented H.264 family accepts.
    private static RateControlMode ConstantQualityMode(EncoderFamily family) =>
        family == EncoderFamily.X264 ? RateControlMode.Crf : RateControlMode.Cqp;

    private static string ConstantQualityModeString(EncoderFamily family) =>
        family == EncoderFamily.X264 ? "CRF" : "CQP";

    // VAAPI names the quantiser "qp"; x264 names it "crf"; NVENC (both key sets), AMF and QSV all name
    // it "cqp", which is also the safest answer for an unknown id.
    private static string QuantiserKey(EncoderFamily family) => family switch
    {
        EncoderFamily.X264 => "crf",
        EncoderFamily.Vaapi => "qp",
        _ => "cqp"
    };

    // Every documented family reads the target bitrate from the same key, in kbps.
    private const string BitrateKey = "bitrate";

    // Only NVENC and QSV document a ceiling key. AMF has none — its VBR ceiling does not exist as a
    // setting — and x264's ceiling is the VBV pair rather than a max_bitrate key, so writing
    // max_bitrate to either would be a key nothing reads.
    private static string? MaxBitrateKey(EncoderFamily family) => family switch
    {
        EncoderFamily.Nvenc or EncoderFamily.Qsv => "max_bitrate",
        _ => null
    };

    // The H.264 video encoder ids the runtime actually registered, in registration order. This is
    // the available set the settings UI offers — a machine without the NVENC plugin never sees the
    // NVENC ids, so unsupported encoders are hidden rather than listed and refused at record time.
    internal static IReadOnlyList<string> EnumerateUsableEncoderIds()
    {
        var ids = new List<string>();
        if (ObsEncoder.IsTypeRegistered(X264Id))
            ids.Add(X264Id);
        foreach (var id in ObsEncoder.EnumerateTypeIds())
        {
            if (!string.Equals(id, X264Id, StringComparison.Ordinal) && IsH264VideoEncoder(id))
                ids.Add(id);
        }

        return ids;
    }

    private static bool IsUsableId(string? id) =>
        !string.IsNullOrWhiteSpace(id) &&
        !string.Equals(id, X264DefaultId, StringComparison.OrdinalIgnoreCase) &&
        IsH264VideoEncoder(id);

    private static bool IsH264VideoEncoder(string id) =>
        ObsEncoder.IsTypeRegistered(id) &&
        ObsEncoder.GetTypeCodec(id) is { } codec &&
        codec.Equals("h264", StringComparison.OrdinalIgnoreCase) &&
        ObsEncoder.GetType(id) == ObsEncoderType.Video;

    // The quality profile in the resolved settings is the app's own 1..20 scale, higher being better.
    // H.264's quantiser scale is 0..51 the other way up, and it is the *same* scale for x264's CRF and
    // for the hardware families' QP/CQP — which is why one mapping serves every family and only the
    // key name differs (ResolveRateControlKeys).
    //
    // What the mapping is not is a straight inversion. It used to be `23 + (20 - quality)`, which put
    // the four presets the settings UI actually offers — 3, 5, 10, 18 — at CRF 40, 38, 33 and 25. For
    // H.264 game footage the useful band is roughly 16 (visually transparent) to 28 (clearly lossy);
    // 33 and above is smeared, so every preset below "Max" produced a recording nobody would keep, and
    // no setting at all reached good quality. Measured on this project's own footage, which is why the
    // anchors below are stated as a table rather than derived from a formula: the interesting part of
    // the curve is not linear, and the preset positions are the points that matter.
    //
    // Anchors, interpolated linearly in between and clamped to H.264's 0..51: quality 3 (Low) is 28,
    // 5 (Medium) 23, 10 (High) 20 and 18 (Max) 16. Monotonic by construction — a higher quality number
    // never yields a higher quantiser — so "more quality" always means "better picture".
    private static readonly (int Quality, int Quantiser)[] QualityAnchors =
    [
        (1, 30),
        (3, 28),
        (5, 23),
        (10, 20),
        (18, 16),
        (20, 15)
    ];

    internal static int MapQualityToQuantiser(int quality)
    {
        var clamped = Math.Clamp(quality, QualityAnchors[0].Quality, QualityAnchors[^1].Quality);

        for (var i = 1; i < QualityAnchors.Length; i++)
        {
            var (highQuality, highQuantiser) = QualityAnchors[i];
            if (clamped > highQuality)
                continue;

            var (lowQuality, lowQuantiser) = QualityAnchors[i - 1];
            var span = highQuality - lowQuality;
            var position = (double)(clamped - lowQuality) / span;
            var quantiser = lowQuantiser + position * (highQuantiser - lowQuantiser);

            // AwayFromZero rather than the banker's rounding Math.Round defaults to: a half-step here
            // is a quantiser step, and "to even" would make the curve wobble rather than descend
            // evenly across the interpolated points.
            return Math.Clamp((int)Math.Round(quantiser, MidpointRounding.AwayFromZero), MinQuantiser, MaxQuantiser);
        }

        return QualityAnchors[^1].Quantiser;
    }

    // H.264's quantiser range. 0 is lossless and 51 is unwatchable; both ends are accepted by every
    // family, and the clamp exists so an out-of-range anchor or a future preset cannot write a value a
    // plugin rejects.
    private const int MinQuantiser = 0;
    private const int MaxQuantiser = 51;

    // The bitrate bounds, in kbps. The floor and ceiling are the tightest the documented families
    // declare — 50 is the floor everywhere, and AMF's 100000 is the lowest ceiling (x264 and QSV allow
    // far more) — so a value clamped here is in range for every family rather than only for the one
    // this machine happens to resolve.
    internal const int MinBitrateKbps = 50;
    internal const int MaxBitrateKbps = 100_000;

    // The fallback target for a settings file whose bitrate is missing or nonsense (0 from an older
    // config, or negative). Clamping a zero to the floor instead would record 50 kbps, which is a
    // worse failure than ignoring the value: it looks configured and produces a slideshow.
    internal const int DefaultBitrateKbps = 15_000;

    internal static int ClampBitrateKbps(int bitrateKbps) =>
        bitrateKbps <= 0 ? DefaultBitrateKbps : Math.Clamp(bitrateKbps, MinBitrateKbps, MaxBitrateKbps);

    // The VBR ceiling. Zero means "derive one", because a VBR ceiling is a detail most users should not
    // have to hold an opinion about: 1.5x the target is the usual headroom, enough for the peaks VBR
    // exists to spend on without letting a busy scene run away with the file size. A ceiling below the
    // target is meaningless, so an explicit value is never allowed under it.
    internal static int ResolveMaxBitrateKbps(int bitrateKbps, int maxBitrateKbps)
    {
        var target = ClampBitrateKbps(bitrateKbps);
        var ceiling = maxBitrateKbps <= 0 ? (int)(target * 1.5) : maxBitrateKbps;
        return Math.Clamp(ceiling, target, MaxBitrateKbps);
    }

    // The encoder families whose key sets differ, keyed by id rather than by runtime version: from OBS
    // 31 several families' key sets are live at once, so "which id did we create" is the only
    // answerable question (spec/obs-binding Part 10). Unknown is a first-class member, not an error —
    // a machine can register an H.264 encoder from a plugin no table here describes, and it must still
    // record.
    private enum EncoderFamily
    {
        X264,
        Vaapi,
        Nvenc,
        Amf,
        Qsv,
        Unknown
    }

    // The keys one rate-control choice resolves to on one encoder family. A null key means the mode
    // does not read that dial on this family, which is why the three keys are nullable rather than
    // empty strings: nothing should ever write a key named "".
    internal readonly record struct EncoderRateControl(
        string Mode,
        string? QuantiserKey,
        string? BitrateKey,
        string? MaxBitrateKey);

    // The IRecorderOutput over a real ObsOutput: forwards start, stop and the stop signal, and owns
    // the lifetime of the output *and* of the two encoders wired into it. The stop signal is
    // marshalled the same way ObsOutput does it — onto the thread that subscribed — so the recorder's
    // own marshalling stays as-is.
    //
    // The encoders are fields rather than locals at the call site because that reachability is the
    // only thing keeping obs_encoder_release out of the GC's hands while the output still holds the
    // pointers; see the ownership note in CreateOutput.
    private sealed class MuxerOutput : IRecorderOutput
    {
        private readonly ObsOutput _output;
        private readonly ObsEncoder _videoEncoder;
        private readonly ObsEncoder? _audioEncoder;
        private readonly AudioRouting? _audioRouting;

        internal MuxerOutput(ObsOutput output, ObsEncoder videoEncoder, ObsEncoder? audioEncoder, AudioRouting? audioRouting)
        {
            _output = output;
            _videoEncoder = videoEncoder;
            _audioEncoder = audioEncoder;
            _audioRouting = audioRouting;
        }

        public bool IsActive => _output.IsActive;

        public bool Start() => _output.Start();

        public void Stop() => _output.Stop();

        public string? LastError => _output.LastError;

        public event EventHandler<ObsOutputStopEvent>? Stopped
        {
            add => _output.Stopped += value;
            remove => _output.Stopped -= value;
        }

        // The output is released first: obs_output_release drops the output while it still holds valid
        // encoder pointers, and only then do the encoders lose their references. The reverse order
        // would leave a live output pointing at released encoders. The audio routing goes last: its
        // dispose deactivates the capture sources (balancing their MarkActive) and releases the
        // track encoders, all after the output has stopped reading the mixes. All these Dispose
        // calls are idempotent — ObsOutput short-circuits on a closed handle and SafeHandle.Dispose
        // is a no-op once run — so a second Dispose here does nothing.
        public void Dispose()
        {
            _output.Dispose();
            _videoEncoder.Dispose();
            _audioEncoder?.Dispose();
            _audioRouting?.Dispose();
        }
    }
}
