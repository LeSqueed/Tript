// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Numerics;
using Serilog;
using Tript.Obs;
using Tript.Settings;

namespace Tript.Recorder;

// The recorder session over the OBS binding: composes the private scene that goes on the recording
// channel, and builds the output — an ffmpeg_muxer, a video encoder at the resolved resolution, and
// the audio encoders. The output is the recorder's to own; the sources are borrowed.
public sealed class ObsRecorderSession : IRecorderSession
{
    private const string FfmpegMuxerId = "ffmpeg_muxer";
    private const string X264Id = "obs_x264";

    private const string GameCaptureId = "game_capture";

    // The settings model's placeholder for "the backend decides"; the real software id is obs_x264.
    private const string X264DefaultId = "x264";

    private const string FfmpegAacId = "ffmpeg_aac";
    private const uint VideoChannel = 0;

    // Seek granularity of every recording, and the only boundary the clip engine can cut on
    // without re-encoding.
    private const int KeyframeIntervalSeconds = 1;

    // obs_source_get_width answers 0 for a game_capture that has not attached, so the hook state
    // costs one call and no new interop. Polled only while the scene is on the recording channel.
    private static readonly TimeSpan HookProbeInterval = TimeSpan.FromSeconds(2);

    // How long a game capture is given before the absence of a hook is reported as such. Only the
    // Auto method uses it: under Game the deadline is the policy's own, because it ends the
    // recording rather than logging a line.
    private static readonly TimeSpan HookTimeout = TimeSpan.FromSeconds(30);

    private readonly ObsSource _source;
    private readonly ObsScene _scene;
    private readonly ObsSource? _displaySource;
    private readonly ObsSource? _gameCaptureSource;
    private readonly ObsSceneItem _colourItem;
    private readonly ObsSceneItem? _displayItem;
    private readonly ObsSceneItem? _gameItem;

    private readonly Lock _probeGate = new();
    private Timer? _hookProbe;
    private int _probeTicks;
    private bool _hooked;
    private bool _hookTimeoutReported;
    private bool _disposed;

    public ObsRecorderSession(ObsRuntime runtime, ObsSource source, ObsGameCaptureTarget? gameCaptureTarget = null,
        CapturePolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(source);

        Runtime = runtime;
        Policy = policy ?? CapturePolicy.Default;
        _source = source.AddReference();

        _scene = ObsScene.CreatePrivate("recorder scene");
        try
        {
            _colourItem = _scene.AddSource(_source)
                ?? throw new ObsException("The recorder scene refused the colour source.");

            // Bottom to top: background, desktop, game. Order is z-order — obs_scene_add always
            // lands on top — so the display layer has to be added before the game capture.
            if (Policy.IncludesDisplayCapture)
            {
                _displaySource = CreateDisplayCaptureSource(Policy.PreferredDisplayId, out var display);
                SelectedDisplay = display;
                if (_displaySource is not null)
                {
                    _displayItem = _scene.AddSource(_displaySource)
                        ?? throw new ObsException("The recorder scene refused the display-capture source.");
                }
            }

            // Created even without an initial target: RetargetGame re-points it without restarting,
            // so a game detected after construction still lands in the recording.
            if (Policy.IncludesGameCapture)
            {
                _gameCaptureSource = CreateGameCaptureSource(gameCaptureTarget);
                if (_gameCaptureSource is not null)
                {
                    _gameItem = _scene.AddSource(_gameCaptureSource)
                        ?? throw new ObsException("The recorder scene refused the game-capture source.");
                }
            }
        }
        catch
        {
            DisposeSceneObjects();
            throw;
        }
    }

    public ObsRuntime Runtime { get; }

    // The capture policy this scene was composed for. A changed policy needs a new session: the
    // layers are created in the constructor.
    public CapturePolicy Policy { get; }

    // The monitor the display layer is capturing, or null when there is no display layer or the
    // runtime enumerated no monitors. Reported rather than saved — a preference that named a
    // monitor which is not attached stays the preference (see ObsCaptureSource.ResolveDisplay).
    public ObsDisplay? SelectedDisplay { get; }

    // Raised once per recording when the Game method's capture has not hooked within the policy's
    // timeout. There is no display layer under it to record instead, so the only honest outcome is
    // to stop; the host owns that decision and this is how it hears about it.
    public event EventHandler<GameCaptureUnavailable>? GameCaptureUnavailable;

    // Whether the game-capture source has actually attached to a process. False on a platform with
    // no game capture, and false whenever the scene is not on the recording channel: win-capture
    // stops its hook while the source is not showing (game-capture.c game_capture_tick).
    public bool IsGameCaptureHooked
    {
        get
        {
            lock (_probeGate)
            {
                return !_disposed && _gameCaptureSource is { } capture && capture.Width > 0;
            }
        }
    }

    // Whether the scene has a display-capture layer. False for the Game method by design, and false
    // when no module registers a display capture — in both cases an unhooked game capture records
    // the colour background and nothing else.
    public bool HasDisplayFallback => _displaySource is not null;

    public IRecorderOutput CreateOutput(ResolvedRecorderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!ObsOutput.IsTypeRegistered(FfmpegMuxerId))
            throw new ObsException($"No loaded module registers the output type '{FfmpegMuxerId}'.");

        if (!Runtime.TryGetVideoHandle(out var video))
            throw new ObsException("The runtime has no video mix; obs_reset_video must succeed before recording.");

        if (!Runtime.TryGetAudioHandle(out var audio))
            throw new ObsException("The runtime has no audio mix; obs_reset_audio must succeed before recording.");

        SizeColourSourceToCanvas(settings.ResolutionWidth, settings.ResolutionHeight);
        FitItemsToCanvas(settings.ResolutionWidth, settings.ResolutionHeight);

        var videoEncoderId = ResolveVideoEncoderId(settings.Encoder);
        if (videoEncoderId is null)
            throw new ObsException("No loaded module registers a usable H.264 video encoder.");

        if (!ObsEncoder.IsTypeRegistered(FfmpegAacId))
            throw new ObsException($"No loaded module registers the audio encoder '{FfmpegAacId}'.");

        var outputSettings = new ObsSettings();
        outputSettings.SetString("path", settings.OutputPath);

        var output = ObsOutput.Create(FfmpegMuxerId, "recorder output", outputSettings);

        // The encoders must stay reachable. obs_output_set_video_encoder only records the pointer,
        // while ObsEncoderHandle holds the sole managed reference and releases it from its finalizer.
        // Left as locals they are collectable the moment this method returns, and the output would
        // then initialize a released encoder at obs_output_start.
        ObsEncoder? videoEncoder = null;
        ObsEncoder? audioEncoder = null;
        AudioRouting? audioRouting = null;

        try
        {
            var rateControl = ResolveRateControl(videoEncoderId, settings.RateControl);
            using (var videoSettings = new ObsSettings())
            {
                videoSettings.SetString("rate_control", rateControl.Mode);

                // Which dial a mode reads is a per-family fact: constant quality reads only the
                // quantiser, CBR only the bitrate, and x264's VBR reads both. x264 zeroes crf under
                // CBR and bitrate under CRF, so the object should say only what the mode means.
                if (rateControl.QuantiserKey is { } quantiserKey)
                    videoSettings.SetInt(quantiserKey, MapQualityToQuantiser(settings.Quality));

                if (rateControl.BitrateKey is { } bitrateKey)
                    videoSettings.SetInt(bitrateKey, ClampBitrateKbps(settings.BitrateKbps));

                if (rateControl.MaxBitrateKey is { } maxBitrateKey)
                    videoSettings.SetInt(maxBitrateKey, ResolveMaxBitrateKbps(settings.BitrateKbps, settings.MaxBitrateKbps));

                // keyint_sec is an interval in SECONDS; the encoder converts it using the mix's
                // frame rate. Passing the frame rate asked for a 60-second GOP that clamped to 10.
                videoSettings.SetInt("keyint_sec", KeyframeIntervalSeconds);

                videoEncoder = ObsEncoder.CreateVideo(videoEncoderId, "recorder video", videoSettings);
                videoEncoder.BindToVideo(video);
                videoEncoder.SetScaledSize((uint)settings.ResolutionWidth, (uint)settings.ResolutionHeight);
                output.SetVideoEncoder(videoEncoder);
            }

            // An empty track list keeps the single programme-mix encoder on slot 0, so a recording
            // with no tracks configured still carries audio.
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
            // An output must never be left alive pointing at a released encoder.
            output.Dispose();
            videoEncoder?.Dispose();
            audioEncoder?.Dispose();
            audioRouting?.Dispose();
            throw;
        }
    }

    public void PlaceSourceOnChannel()
    {
        Runtime.SetOutputSource(VideoChannel, _scene);
        StartHookProbe();
    }

    public void ClearSourceFromChannel()
    {
        StopHookProbe();
        Runtime.SetOutputSource(VideoChannel, (ObsSource?)null);
    }

    // Re-points the live game-capture source without restarting it; obs_source_update merges, so
    // the capture_mode it was created with survives. False when the platform has no game capture.
    public bool RetargetGame(ObsGameCaptureTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (_gameCaptureSource is null || !ObsCaptureSource.Retarget(_gameCaptureSource, target))
            return false;

        Log.Information("ObsRecorderSession: game capture re-targeted at {Window}",
            ObsCaptureSource.BuildWindowMatchString(target));
        return true;
    }

    // Scene items first — each holds a reference to its source and to its scene — then the scene,
    // then the sources.
    public void Dispose()
    {
        StopHookProbe();

        lock (_probeGate)
            _disposed = true;

        DisposeSceneObjects();
    }

    private void DisposeSceneObjects()
    {
        _gameItem?.Dispose();
        _displayItem?.Dispose();
        _colourItem?.Dispose();
        _scene.Dispose();
        _source.Dispose();
        _displaySource?.Dispose();
        _gameCaptureSource?.Dispose();
    }

    // ---- scene composition ----

    // The desktop layer. Null when no loaded module registers a display capture, which is a degraded
    // but working state: the recording is then the colour background until the game capture hooks.
    private static ObsSource? CreateDisplayCaptureSource(string? preferredDisplayId, out ObsDisplay? selected)
    {
        selected = null;

        if (ObsCaptureSource.FindDisplayCaptureId() is not { } displayId)
        {
            Log.Warning("ObsRecorderSession: no display-capture source type is registered; " +
                        "an unhooked game capture will record the background only.");
            return null;
        }

        var source = ObsSource.CreatePrivate(displayId, "app display");
        try
        {
            // Through the instance rather than the type: monitor_capture's "monitor_id" default is
            // the sentinel "DUMMY", which matches no monitor and captures nothing, so the chosen
            // monitor has to be written back explicitly.
            var displays = ObsCaptureSource.EnumerateDisplays(source);
            var resolution = ObsCaptureSource.ResolveDisplay(displays, preferredDisplayId);
            selected = resolution.Selected;

            if (resolution.RequestedMissing)
            {
                Log.Warning("ObsRecorderSession: the selected monitor {RequestedId} is not attached; " +
                            "capturing {UsingName} ({UsingId}) for this session. The preference is kept.",
                    preferredDisplayId, selected?.Name, selected?.Id);
            }

            if (selected is null)
            {
                // Enumerating nothing is an ordinary state, not a failure: the source keeps whatever
                // the plugin defaults to and the recording proceeds.
                Log.Warning("ObsRecorderSession: {DisplayId} enumerated no monitors; " +
                            "the display layer keeps the plugin's default.", displayId);
            }
            else
            {
                using var settings = ObsCaptureSource.BuildDisplayCaptureSettings(source, selected);
                source.Update(settings);
                Log.Information("ObsRecorderSession: display layer is {DisplayId} on {Name} ({Id}) {Width}x{Height}",
                    displayId, selected.Name, selected.Id, selected.Width, selected.Height);
            }
        }
        catch
        {
            source.Dispose();
            throw;
        }

        return source;
    }

    // Null when the platform has no game-capture source (Linux), which the caller treats as display
    // capture only. A null target still creates the source so RetargetGame has something to
    // re-point.
    private static ObsSource? CreateGameCaptureSource(ObsGameCaptureTarget? target)
    {
        var properties = ObsSourceProperties.EnumerateTypeProperties(GameCaptureId);
        if (properties.Count == 0)
            return null;

        using var settings = target is not null
            ? ObsCaptureSource.BuildGameCaptureSettings(target) ?? new ObsSettings()
            : new ObsSettings();

        // Written even when the property was not discovered: the plugin's default is any_fullscreen,
        // which hooks whatever happens to be fullscreen and ignores the window string entirely, and
        // a key a plugin does not declare is simply ignored. There is no path that leaves it unset.
        if (ResolveWindowCaptureMode(properties) is { } mode)
        {
            settings.SetString(CaptureModeKey, mode);
        }
        else
        {
            Log.Warning("ObsRecorderSession: game capture declares no '{Key}' property; " +
                        "writing '{Value}' anyway rather than leaving it on any_fullscreen.",
                CaptureModeKey, WindowCaptureModeValue);
            settings.SetString(CaptureModeKey, WindowCaptureModeValue);
        }

        return ObsSource.CreatePrivate(GameCaptureId, "app capture", settings);
    }

    // win-capture's mode key, and the value that makes it match on the window string rather than
    // grabbing whatever is fullscreen in the foreground.
    private const string CaptureModeKey = "capture_mode";
    private const string WindowCaptureModeValue = "window";

    // Without this the plugin's default capture_mode is "any_fullscreen": it hooks whichever
    // fullscreen window is foreground and ignores the window string entirely. The value is
    // plugin-side, so it is taken from the property's own item list rather than assumed — but the
    // property is found by its declared key, because game capture's window list also carries items
    // spelled "window" and the first list that does is not necessarily this one.
    internal static string? ResolveWindowCaptureMode(IReadOnlyList<ObsSourceProperty> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);

        foreach (var property in properties)
        {
            if (property.Type != ObsPropertyType.List ||
                !string.Equals(property.Name, CaptureModeKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var windowMode = property.Items.FirstOrDefault(item =>
                item.Format == ObsComboFormat.String &&
                item.Value is string itemValue &&
                string.Equals(itemValue, WindowCaptureModeValue, StringComparison.Ordinal));

            return windowMode.Value as string ?? WindowCaptureModeValue;
        }

        return null;
    }

    // ---- hook visibility ----

    // The game item is deliberately never hidden while it waits for a hook. An invisible scene item
    // drops the source's showing reference, and win-capture stops — and never starts — its hook
    // while the source is not showing.
    private void StartHookProbe()
    {
        // A Game-method session still probes without a game-capture source — a platform that
        // registers none can never hook, and that has to reach the deadline rather than record a
        // black file in silence.
        if (_gameCaptureSource is null && !Policy.StopsWhenUnhooked)
            return;

        lock (_probeGate)
        {
            if (_disposed || _hookProbe is not null)
                return;

            _hooked = false;
            _probeTicks = 0;
            _hookTimeoutReported = false;
            _hookProbe = new Timer(ProbeHook, null, HookProbeInterval, HookProbeInterval);
        }
    }

    private void StopHookProbe()
    {
        Timer? probe;
        lock (_probeGate)
        {
            probe = _hookProbe;
            _hookProbe = null;
        }

        probe?.Dispose();
    }

    private void ProbeHook(object? state)
    {
        GameCaptureUnavailable? unavailable = null;

        lock (_probeGate)
        {
            if (_disposed || _hookProbe is null)
                return;

            _probeTicks++;
            var hooked = _gameCaptureSource is { Width: > 0 };

            if (hooked != _hooked)
            {
                _hooked = hooked;
                if (hooked)
                {
                    Log.Information("ObsRecorderSession: game capture hooked at {Width}x{Height}",
                        _gameCaptureSource!.Width, _gameCaptureSource.Height);
                }
                else if (Policy.IncludesDisplayCapture)
                {
                    Log.Warning("ObsRecorderSession: game capture lost its hook; recording the display fallback.");
                }
                else
                {
                    Log.Warning("ObsRecorderSession: game capture lost its hook and there is no display layer.");
                }
            }

            // Said once, because a hook that has not happened by now is the shape of the failure
            // that otherwise reads as a working recording of the wrong picture. Under the Game
            // method it is not a warning at all: there is nothing under the capture, so the
            // recording would be black and the host is told to end it.
            var deadline = HookDeadlineFor(Policy);
            if (!hooked && !_hookTimeoutReported && _probeTicks * HookProbeInterval >= deadline)
            {
                _hookTimeoutReported = true;

                if (Policy.StopsWhenUnhooked)
                {
                    unavailable = new GameCaptureUnavailable(deadline,
                        $"Game capture did not attach within {deadline.TotalSeconds:0}s and the capture method is " +
                        "game capture only, so the recording was stopped rather than recorded black.");
                }
                else
                {
                    Log.Warning("ObsRecorderSession: game capture has not hooked after {Seconds}s; " +
                                "the recording is the display fallback.", deadline.TotalSeconds);
                }
            }
        }

        // Outside the gate: the handler stops the recording, which clears the channel and disposes
        // this very timer.
        if (unavailable is not null)
        {
            Log.Warning("ObsRecorderSession: {Message}", unavailable.Message);
            GameCaptureUnavailable?.Invoke(this, unavailable);
        }
    }

    // The Game method's deadline is the user's configured game-capture timeout; every other method
    // has a display layer showing meanwhile, so its deadline only governs a log line.
    internal static TimeSpan HookDeadlineFor(CapturePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.StopsWhenUnhooked ? policy.GameCaptureTimeout : HookTimeout;
    }

    // ---- canvas fit ----

    // With no width/height setting the colour source renders at the plugin's default block size
    // instead of filling the canvas.
    private void SizeColourSourceToCanvas(int width, int height)
    {
        using var settings = _source.GetSettings();
        if (settings.GetInt("width") >= width && settings.GetInt("height") >= height)
            return;

        settings.SetInt("width", width);
        settings.SetInt("height", height);
        _source.Update(settings);
    }

    // Every item is fitted to the canvas instead of being drawn 1:1 at the origin: a capture is
    // whatever resolution its monitor or its game window happens to be, and an unbounded item at a
    // different resolution is either cropped or a picture in the corner of a black frame. The scene
    // renders at the mix's BASE size — the encoder does the scale to the output size — so that, not
    // the requested recording resolution, is the box to fit to.
    private void FitItemsToCanvas(int fallbackWidth, int fallbackHeight)
    {
        var width = (float)fallbackWidth;
        var height = (float)fallbackHeight;

        if (Runtime.TryGetVideoInfo(out var video) && video is not null && video.BaseWidth > 0 && video.BaseHeight > 0)
        {
            width = video.BaseWidth;
            height = video.BaseHeight;
        }

        FitToCanvas(_colourItem, width, height);
        FitToCanvas(_displayItem, width, height);
        FitToCanvas(_gameItem, width, height);
    }

    // The individual bounds setters rather than the whole-transform one: obs_sceneitem_set_info2 is
    // an OBS 30.1 entry point and these are not, so this path still works against an older libobs.
    // A runtime missing even these is left unbounded rather than failing the recording.
    private static void FitToCanvas(ObsSceneItem? item, float width, float height)
    {
        if (item is null)
            return;

        try
        {
            using (item.DeferUpdates())
            {
                item.Alignment = ObsAlignment.Left | ObsAlignment.Top;
                item.Position = Vector2.Zero;
                item.BoundsType = ObsBoundsType.ScaleInner;
                item.BoundsAlignment = ObsAlignment.Center;
                item.Bounds = new Vector2(width, height);
            }
        }
        catch (EntryPointNotFoundException exception)
        {
            Log.Warning(exception, "ObsRecorderSession: this libobs has no scene-item bounds API; " +
                                   "the scene renders unbounded.");
        }
    }

    // Which H.264 encoder the runtime actually registered; the ids differ by machine. An explicit
    // user choice wins while it is still registered, but the model's "x264" placeholder does not
    // count as one.
    internal static string? ResolveVideoEncoderId(string? configuredEncoder)
    {
        if (IsUsableId(configuredEncoder))
            return configuredEncoder;

        var usable = EnumerateUsableEncoderIds();
        return usable.FirstOrDefault(id => !string.Equals(id, X264Id, StringComparison.Ordinal))
               ?? usable.FirstOrDefault();
    }

    // The rate-control mode and constant-quality key an encoder family reads, keyed by id rather
    // than by runtime version: from OBS 31 several families' key sets are live at once. Getting it
    // wrong is not always silent. obs-ffmpeg matches rate_control against a NULL-terminated table
    // with astrcmpi and, on no match, dereferences the terminator in strcmp: writing x264's "CRF"
    // to ffmpeg_vaapi segfaults inside obs_output_start.
    internal static (string RateControl, string QualityKey) ResolveRateControlKeys(string encoderId)
    {
        ArgumentException.ThrowIfNullOrEmpty(encoderId);

        var family = ClassifyFamily(encoderId);
        return (ConstantQualityModeString(family), QuantiserKey(family));
    }

    // Which rate-control modes a family accepts, in the order the settings UI should offer them. A
    // mode outside the list is the segfault documented on ResolveRateControlKeys, not a setting the
    // plugin ignores. Three asymmetries are the reason this is a table:
    //
    //  * CRF exists only on x264; the hardware families spell constant quality "CQP".
    //  * VAAPI is withheld from VBR — its ceiling key is unverified here, and a mistyped ceiling key
    //    fails silently at the wrong bitrate.
    //  * An id no table describes gets constant quality plus CBR, the two modes every documented
    //    family accepts.
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

    // A settings file has to travel: a config written on a software-only machine carries Crf, and
    // on an NVIDIA machine the resolved family rejects it. The user's mode is a request; the family
    // has the final say.
    internal static RateControlMode CoerceRateControlMode(string encoderId, RateControlMode requested)
    {
        ArgumentException.ThrowIfNullOrEmpty(encoderId);

        return SupportedRateControlModes(encoderId).Contains(requested)
            ? requested
            : ConstantQualityMode(ClassifyFamily(encoderId));
    }

    // The mode string and the keys to write for a requested mode on a given id. A null key means
    // the mode does not read that dial on this family, which is not the same as zero.
    internal static EncoderRateControl ResolveRateControl(string encoderId, RateControlMode requested)
    {
        ArgumentException.ThrowIfNullOrEmpty(encoderId);

        var family = ClassifyFamily(encoderId);
        var mode = CoerceRateControlMode(encoderId, requested);

        return mode switch
        {
            RateControlMode.Crf or RateControlMode.Cqp =>
                new EncoderRateControl(ConstantQualityModeString(family), QuantiserKey(family), null, null),

            // CBR needs no ceiling: max_bitrate is a VBR-only key, and AMF has none at all.
            RateControlMode.Cbr => new EncoderRateControl("CBR", null, BitrateKey, null),

            // x264's VBR is a CRF target with a VBV cap, so it is the one family reading the
            // quantiser and the bitrate together; its ceiling is the VBV pair (use_bufsize plus
            // buffer_size) rather than a max_bitrate key.
            RateControlMode.Vbr => new EncoderRateControl(
                "VBR",
                family == EncoderFamily.X264 ? QuantiserKey(family) : null,
                BitrateKey,
                MaxBitrateKey(family)),

            _ => throw new ArgumentOutOfRangeException(nameof(requested), requested, "Unknown rate-control mode.")
        };
    }

    // x264 is matched exactly and the rest by substring: the hardware families each ship several
    // ids, while "contains x264" would also catch a third-party id that merely mentions it — and
    // mistaking something else for x264 is the one error that writes "CRF" to a family that
    // segfaults on it. The substring matches ignore case, or FFMPEG_VAAPI would fall through.
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

    // x264 is the only family whose constant-quality mode is CRF.
    private static RateControlMode ConstantQualityMode(EncoderFamily family) =>
        family == EncoderFamily.X264 ? RateControlMode.Crf : RateControlMode.Cqp;

    private static string ConstantQualityModeString(EncoderFamily family) =>
        family == EncoderFamily.X264 ? "CRF" : "CQP";

    // VAAPI names the quantiser "qp"; x264 names it "crf"; NVENC, AMF and QSV all name it "cqp".
    private static string QuantiserKey(EncoderFamily family) => family switch
    {
        EncoderFamily.X264 => "crf",
        EncoderFamily.Vaapi => "qp",
        _ => "cqp"
    };

    // Every documented family reads the target bitrate from the same key, in kbps.
    private const string BitrateKey = "bitrate";

    // Only NVENC and QSV document a ceiling key. AMF has none, and x264's ceiling is the VBV pair.
    private static string? MaxBitrateKey(EncoderFamily family) => family switch
    {
        EncoderFamily.Nvenc or EncoderFamily.Qsv => "max_bitrate",
        _ => null
    };

    // The available set the settings UI offers, so an encoder this machine cannot use is hidden
    // rather than listed and refused at record time.
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

    // The app's quality profile is 1..20, higher better; H.264's quantiser is 0..51 the other way
    // up, and it is the same scale for x264's CRF and the hardware families' QP/CQP. Not a straight
    // inversion.
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

            // AwayFromZero, not the banker's rounding Math.Round defaults to: a half-step here is a
            // quantiser step, and "to even" would make the curve wobble rather than descend evenly.
            return Math.Clamp((int)Math.Round(quantiser, MidpointRounding.AwayFromZero), MinQuantiser, MaxQuantiser);
        }

        return QualityAnchors[^1].Quantiser;
    }

    // H.264's quantiser range: 0 is lossless, 51 unwatchable. The clamp keeps an out-of-range
    // anchor or a future preset off a plugin that would reject it.
    private const int MinQuantiser = 0;
    private const int MaxQuantiser = 51;

    // The tightest bounds the documented families declare — 50 is the floor everywhere and AMF's
    // 100000 the lowest ceiling — so a clamped value is in range for every family, not just this one.
    internal const int MinBitrateKbps = 50;
    internal const int MaxBitrateKbps = 100_000;

    // The fallback for a bitrate that is missing or nonsense. Clamping a zero to the floor instead
    // would record 50 kbps: configured-looking, and a slideshow.
    internal const int DefaultBitrateKbps = 15_000;

    internal static int ClampBitrateKbps(int bitrateKbps) =>
        bitrateKbps <= 0 ? DefaultBitrateKbps : Math.Clamp(bitrateKbps, MinBitrateKbps, MaxBitrateKbps);

    // Zero means "derive one": 1.5x the target is the usual VBR headroom. A ceiling below the target
    // is meaningless, so an explicit value is never allowed under it.
    internal static int ResolveMaxBitrateKbps(int bitrateKbps, int maxBitrateKbps)
    {
        var target = ClampBitrateKbps(bitrateKbps);
        var ceiling = maxBitrateKbps <= 0 ? (int)(target * 1.5) : maxBitrateKbps;
        return Math.Clamp(ceiling, target, MaxBitrateKbps);
    }

    // Keyed by id rather than by runtime version: from OBS 31 several families' key sets are live at
    // once. Unknown is a first-class member, not an error — a machine can register an H.264 encoder
    // from a plugin no table here describes, and it must still record.
    private enum EncoderFamily
    {
        X264,
        Vaapi,
        Nvenc,
        Amf,
        Qsv,
        Unknown
    }

    // A null key means the mode does not read that dial on this family, which is why the keys are
    // nullable rather than empty strings.
    internal readonly record struct EncoderRateControl(
        string Mode,
        string? QuantiserKey,
        string? BitrateKey,
        string? MaxBitrateKey);

// The IRecorderOutput over a real ObsOutput: forwards start, stop and the stop signal, and owns the
// output and the two encoders wired into it. The encoders are fields rather than locals for the
// reachability reason documented in CreateOutput.
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

        // Release order matters: the output first, while its encoder pointers are still valid, then
        // the encoders, then the audio routing — whose dispose deactivates the capture sources only
        // after the output has stopped reading the mixes.
        public void Dispose()
        {
            _output.Dispose();
            _videoEncoder.Dispose();
            _audioEncoder?.Dispose();
            _audioRouting?.Dispose();
        }
    }
}
