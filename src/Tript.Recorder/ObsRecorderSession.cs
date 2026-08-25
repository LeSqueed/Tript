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
    private const string ReplayBufferId = "replay_buffer";
    private const string GameCaptureId = "game_capture";

    private const string FfmpegAacId = "ffmpeg_aac";
    private const uint VideoChannel = 0;

    // Seek granularity of every recording, and the only boundary the clip engine can cut on
    // without re-encoding.
    private const int KeyframeIntervalSeconds = 1;

    // obs_source_get_width answers 0 for a game_capture that has not attached, so the hook state
    // costs one call and no new interop. Polled only while the scene is on the recording channel.
    private static readonly TimeSpan HookProbeInterval = TimeSpan.FromSeconds(2);

    // How long a Game capture is allowed to wait before the user sees a warning. The wait itself is
    // unbounded because the game window may appear well after the process starts.
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
        var session = CreateEncodedOutput(settings, FfmpegMuxerId, "recorder output");
        if (!settings.Mode.UsesReplayBuffer())
            return session;

        try
        {
            var replay = CreateEncodedOutput(settings, ReplayBufferId, "replay buffer",
                includeCaptureSources: false);
            return new CombinedRecorderOutput(session, replay);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private MuxerOutput CreateEncodedOutput(ResolvedRecorderSettings settings, string outputId, string outputName,
        bool includeCaptureSources = true)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!ObsOutput.IsTypeRegistered(outputId))
            throw new ObsException($"No loaded module registers the output type '{outputId}'.");

        // Before anything binds to the mix: obs_reset_video answers CurrentlyActive once an output
        // exists, so the canvas colour space is settled here or not at all.
        var plan = ResolveHdrPlan(settings);
        plan = ApplyCanvasColour(plan);
        ApplyCaptureColour(plan);

        if (!Runtime.TryGetVideoHandle(out var video))
            throw new ObsException("The runtime has no video mix; obs_reset_video must succeed before recording.");

        if (!Runtime.TryGetAudioHandle(out var audio))
            throw new ObsException("The runtime has no audio mix; obs_reset_audio must succeed before recording.");

        SizeColourSourceToCanvas(settings.ResolutionWidth, settings.ResolutionHeight);
        FitItemsToCanvas(settings.ResolutionWidth, settings.ResolutionHeight);

        var videoEncoderId = plan.EncoderId;

        if (!ObsEncoder.IsTypeRegistered(FfmpegAacId))
            throw new ObsException($"No loaded module registers the audio encoder '{FfmpegAacId}'.");

        using var outputSettings = new ObsSettings();
        if (outputId == FfmpegMuxerId)
        {
            outputSettings.SetString("path", settings.OutputPath);
        }
        else
        {
            outputSettings.SetInt("max_time_sec", (long)Math.Max(1, settings.BufferDuration.TotalSeconds));
            outputSettings.SetInt("max_size_mb", Math.Max(1, (int)(settings.BufferMaxSizeBytes / (1024 * 1024))));
            outputSettings.SetString("directory", Path.GetDirectoryName(settings.OutputPath));
            outputSettings.SetString("format", "tript-replay-%CCYY-%MM-%DD-%hh-%mm-%ss");
            outputSettings.SetString("extension", "mp4");
            outputSettings.SetBool("allow_spaces", false);
        }

        var output = ObsOutput.Create(outputId, outputName, outputSettings);

        // The encoders must stay reachable. obs_output_set_video_encoder only records the pointer,
        // while ObsEncoderHandle holds the sole managed reference and releases it from its finalizer.
        // Left as locals they are collectable the moment this method returns, and the output would
        // then initialize a released encoder at obs_output_start.
        ObsEncoder? videoEncoder = null;
        ObsEncoder? audioEncoder = null;
        AudioRouting? audioRouting = null;

        try
        {
            var rateControl = ObsEncoderPolicy.ResolveRateControl(videoEncoderId, settings.RateControl);
            using (var videoSettings = new ObsSettings())
            {
                videoSettings.SetString("rate_control", rateControl.Mode);

                // Which dial a mode reads is a per-family fact: constant quality reads only the
                // quantiser, CBR only the bitrate, and x264's VBR reads both. x264 zeroes crf under
                // CBR and bitrate under CRF, so the object should say only what the mode means.
                if (rateControl.QuantiserKey is { } quantiserKey)
                    videoSettings.SetInt(quantiserKey, ObsEncoderPolicy.MapQualityToQuantiser(settings.Quality));

                if (rateControl.BitrateKey is { } bitrateKey)
                    videoSettings.SetInt(bitrateKey, ObsEncoderPolicy.ClampBitrateKbps(settings.BitrateKbps));

                if (rateControl.MaxBitrateKey is { } maxBitrateKey)
                    videoSettings.SetInt(maxBitrateKey,
                        ObsEncoderPolicy.ResolveMaxBitrateKbps(settings.BitrateKbps, settings.MaxBitrateKbps));

                // keyint_sec is an interval in SECONDS; the encoder converts it using the mix's
                // frame rate. Passing the frame rate asked for a 60-second GOP that clamped to 10.
                videoSettings.SetInt("keyint_sec", KeyframeIntervalSeconds);

                // HEVC carries ten bits only under main10; an HDR mix into "main" is truncated to
                // eight and the PQ transfer then describes a file that no longer holds its range.
                if (plan.Profile is { } profile)
                    videoSettings.SetString("profile", profile);

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
                audioRouting = new AudioRoutingService(sink).Wire(
                    AudioRoutingPlanner.Plan(settings.AudioTracks), includeCaptureSources);
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

            return new MuxerOutput(output, videoEncoder, audioEncoder, audioRouting, outputId == ReplayBufferId);
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

        // win-capture waits 10 seconds before its first hook attempt when a source becomes visible
        // at the default Normal rate. Tript already knows the exact executable and waits for a real
        // captured frame before starting output, so use OBS's Fastest rate (10s * 0.1 = 1s). This
        // changes only the retry cadence; target-startup protection and indefinite retries remain
        // inside win-capture.
        settings.SetInt(HookRateKey, FastestHookRate);

        return ObsSource.CreatePrivate(GameCaptureId, "app capture", settings);
    }

    // win-capture's mode key, and the value that makes it match on the window string rather than
    // grabbing whatever is fullscreen in the foreground.
    private const string CaptureModeKey = "capture_mode";
    private const string WindowCaptureModeValue = "window";
    private const string HookRateKey = "hook_rate";
    private const long FastestHookRate = 3;

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
        // A platform that registers no game-capture source cannot hook, so there is nothing to
        // probe. The host skips preflight for that case rather than waiting forever.
        if (_gameCaptureSource is null)
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
                    if (_hookTimeoutReported)
                        Log.Information("ObsRecorderSession: late game-capture warning cleared; hook recovered.");
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
            // that otherwise reads as a working recording of the wrong picture. The game may still
            // be loading in the background, so this is diagnostic only and never ends the recording.
            var deadline = HookDeadlineFor(Policy);
            if (!hooked && !_hookTimeoutReported && _probeTicks * HookProbeInterval >= deadline)
            {
                _hookTimeoutReported = true;
                Log.Warning("ObsRecorderSession: game capture has not hooked after {Seconds}s; " +
                            "continuing to retry while recording.", deadline.TotalSeconds);
            }
        }
    }

    public bool HasGameCaptureSource => _gameCaptureSource is not null;

    // Shows the scene without starting an output so win-capture can attach before the recording
    // begins. The wait is intentionally unbounded; cancellation is reserved for host teardown.
    public bool WaitForGameCapture(TimeSpan warningAfter, Action showWarning, Action clearWarning,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(showWarning);
        ArgumentNullException.ThrowIfNull(clearWarning);

        if (_gameCaptureSource is null)
            return true;

        var started = DateTime.UtcNow;
        var warningShown = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (IsGameCaptureHooked)
            {
                if (warningShown)
                    clearWarning();
                return true;
            }

            if (!warningShown && DateTime.UtcNow - started >= warningAfter)
            {
                warningShown = true;
                showWarning();
            }

            lock (_probeGate)
            {
                if (_disposed)
                    return false;
            }

            if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(250)))
                break;
        }

        return false;
    }

    // The Game method's deadline is the user's configured game-capture timeout; every other method
    // has a display layer showing meanwhile, so its deadline only governs a log line.
    internal static TimeSpan HookDeadlineFor(CapturePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.Method == DisplayCaptureMethod.Game ? policy.GameCaptureTimeout : HookTimeout;
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

    // What this machine can actually do with the colour the captured display is in.
    private HdrPlan ResolveHdrPlan(ResolvedRecorderSettings settings)
    {
        var candidates = ObsEncoderPolicy.EnumerateVideoEncoderCandidates();
        if (candidates.Count == 0)
            throw new ObsException("No loaded module registers a usable video encoder.");

        // The configured id only counts when it is one this machine registered; the settings model's
        // "x264" placeholder is a request for the default, not an encoder id.
        var configured = ObsEncoderPolicy.IsUsableId(settings.Encoder) ? settings.Encoder : null;

        var plan = HdrPlanner.Decide(
            CaptureColourSpace(),
            HdrDisplayProbe.AnyDisplayIsHdr(),
            settings.EnableHdr,
            candidates,
            configured);

        Log.Information("ObsRecorderSession: recording in {Colour} with '{Encoder}' — {Reason}.",
            plan.UseHdr ? "HDR (Rec.2100 PQ, 10-bit P010)" : "SDR (Rec.709)", plan.EncoderId, plan.Reason);

        return plan;
    }

    // The colour the recording is being made in, from the desktop duplicator — the one Windows
    // signal that reports it honestly. The capture SOURCES do not: game capture answers Srgb while
    // hooked to a game presenting scRGB, and monitor capture answers Srgb for a Rec.2100 PQ desktop.
    //
    // The DISPLAY is deliberately the input, not the game, and not only because the game will not
    // say. A display's HDR mode is stable for the length of a recording; a game's is not — it can be
    // toggled in the game's own settings mid-session, and obs_reset_video refuses once an output is
    // running, so a canvas chosen from the game can go stale with no way to correct it. A canvas
    // chosen from the display cannot. The mismatch that a mid-recording toggle then creates is
    // absorbed by the compositor's own conversion, which works now that the video levels are set.
    //
    // The probe also carries the display's real SDR white level, which is the number that conversion
    // is done with, so it is applied here rather than left at the generic default.
    private ObsSourceColorSpace? CaptureColourSpace()
    {
        var index = SelectedDisplay?.Index ?? 0;
        if (Runtime.ProbeDisplay(index) is not { } display)
            return null;

        // Guarded, because zero is the value that started all of this: a white level of zero makes
        // every conversion between colour spaces black, and a probe that answered zero would put it
        // straight back. The reset's default stands in that case.
        if (display.SdrWhiteLevelNits > 0f)
            Runtime.SetVideoLevels(display.SdrWhiteLevelNits, ObsRuntime.DefaultHdrNominalPeakLevelNits);

        Log.Debug("ObsRecorderSession: display {Index} reports {Space} at {Nits} nits SDR white.",
            index, display.ColorSpace, display.SdrWhiteLevelNits);

        return display.ColorSpace;
    }

    // Moves the canvas onto the plan's format and colour space, keeping every other dimension of the
    // mix as it was. A refused reset is not fatal: the canvas is still the SDR one that was working,
    // so the plan is downgraded to match rather than recording HDR metadata over SDR pixels.
    private HdrPlan ApplyCanvasColour(HdrPlan plan)
    {
        if (!Runtime.TryGetVideoInfo(out var current) || current is null)
            return plan;

        if (current.OutputFormat == plan.OutputFormat && current.ColorSpace == plan.ColorSpace)
            return plan;

        var result = Runtime.ResetVideo(current with
        {
            OutputFormat = plan.OutputFormat,
            ColorSpace = plan.ColorSpace
        });

        if (result == ObsVideoResetResult.Success)
        {
            // obs_reset_video rebuilds the mix. Re-asserting the program source costs nothing and
            // removes any question about whether the scene survived the rebuild.
            PlaceSourceOnChannel();
            return plan;
        }

        if (!plan.UseHdr)
        {
            // Refused on the way back to SDR: the mix stays where it is, and the capture sources are
            // still told to tonemap, so the frame remains watchable.
            Log.Warning("ObsRecorderSession: obs_reset_video refused the SDR canvas ({Result}); " +
                        "the mix keeps its current colour space.", result);
            return plan;
        }

        Log.Warning("ObsRecorderSession: obs_reset_video refused the HDR canvas ({Result}); " +
                    "recording SDR instead.", result);

        // Forced to SDR outright rather than re-asked: the canvas the mix still has is the SDR one,
        // so the plan has to match that fact and not the content's preference.
        var downgraded = HdrPlanner.Decide(
            capturedColorSpace: ObsSourceColorSpace.Srgb,
            displayIsHdr: false,
            hdrEnabledInSettings: false,
            ObsEncoderPolicy.EnumerateVideoEncoderCandidates(),
            configuredEncoderId: null);

        ApplyCaptureColour(downgraded);
        return downgraded;
    }

    // Tells the capture sources whether they are feeding an SDR canvas. This is the line that makes
    // an HDR game record at all when we are not recording HDR: win-capture hands over the game's
    // FP16 scRGB texture untouched otherwise, and Rec.709 has nowhere to put it.
    private void ApplyCaptureColour(HdrPlan plan)
    {
        ApplyForceSdr(_gameCaptureSource, plan.ForceSdrOnCapture);
        ApplyForceSdr(_displaySource, plan.ForceSdrOnCapture);
    }

    private static void ApplyForceSdr(ObsSource? source, bool forceSdr)
    {
        if (source is null)
            return;

        try
        {
            using var settings = new ObsSettings();
            settings.SetBool(ForceSdrKey, forceSdr);
            source.Update(settings);
        }
        catch (ObsException exception)
        {
            // A capture plugin without the key ignores it; only a failure to talk to the source at
            // all lands here, and that is not worth failing a recording over.
            Log.Debug(exception, "ObsRecorderSession: could not set '{Key}' on a capture source.", ForceSdrKey);
        }
    }

    // win-capture's key on both game and monitor capture. Absent on the Linux sources, where it is
    // simply ignored — there is no HDR desktop path there to disagree with.
    private const string ForceSdrKey = "force_sdr";

// The IRecorderOutput over a real ObsOutput: forwards start, stop and the stop signal, and owns the
// output and the two encoders wired into it. The encoders are fields rather than locals for the
// reachability reason documented in CreateOutput.
    private sealed class MuxerOutput : IRecorderOutput, IReplayBufferOutput
    {
        private readonly ObsOutput _output;
        private readonly ObsEncoder _videoEncoder;
        private readonly ObsEncoder? _audioEncoder;
        private readonly AudioRouting? _audioRouting;
        private readonly bool _isReplayBuffer;
        private readonly object _replayGate = new();
        private readonly ManualResetEventSlim _replaySaveCompleted = new(true);
        private Action<string>? _replaySaved;
        private bool _replaySavePending;

        internal MuxerOutput(ObsOutput output, ObsEncoder videoEncoder, ObsEncoder? audioEncoder,
            AudioRouting? audioRouting, bool isReplayBuffer)
        {
            _output = output;
            _videoEncoder = videoEncoder;
            _audioEncoder = audioEncoder;
            _audioRouting = audioRouting;
            _isReplayBuffer = isReplayBuffer;
            if (_isReplayBuffer)
                _output.Saved += OnReplaySaved;
        }

        public bool IsActive => _output.IsActive;

        public bool Start() => _output.Start();

        public void Stop() => _output.Stop();

        public bool WaitForStop(TimeSpan timeout) => _output.WaitForStop(timeout);

        public string? LastError => _output.LastError;

        public event EventHandler<ObsOutputStopEvent>? Stopped
        {
            add => _output.Stopped += value;
            remove => _output.Stopped -= value;
        }

        public bool SaveReplay(string directory, string format, Action<string> onSaved)
        {
            if (!_isReplayBuffer)
                return false;

            lock (_replayGate)
            {
                if (_replaySavePending)
                    return false;

                using var settings = _output.GetSettings();
                settings.SetString("directory", directory);
                settings.SetString("format", format);
                settings.SetString("extension", "mp4");
                settings.SetBool("allow_spaces", false);
                _output.Update(settings);
                _replaySaved = onSaved;
                _replaySavePending = true;
                _replaySaveCompleted.Reset();
                if (_output.CallProcedure("save"))
                    return true;

                _replaySaved = null;
                _replaySavePending = false;
                _replaySaveCompleted.Set();
                return false;
            }
        }

        private void OnReplaySaved(object? sender, EventArgs args)
        {
            try
            {
                Action<string>? callback;
                string? path;
                lock (_replayGate)
                {
                    callback = _replaySaved;
                    path = callback is null ? null : _output.CallStringProcedure("get_last_replay", "path");
                    _replaySaved = null;
                    _replaySavePending = false;
                }

                if (callback is not null && !string.IsNullOrWhiteSpace(path))
                    callback(path);
            }
            finally
            {
                _replaySaveCompleted.Set();
            }
        }

        public bool WaitForReplaySave(TimeSpan timeout) => _replaySaveCompleted.Wait(timeout);

        // Release order matters: the output first, while its encoder pointers are still valid, then
        // the encoders, then the audio routing — whose dispose deactivates the capture sources only
        // after the output has stopped reading the mixes.
        public void Dispose()
        {
            if (_isReplayBuffer)
                _replaySaveCompleted.Wait(Timeout.InfiniteTimeSpan);
            if (_isReplayBuffer)
                _output.Saved -= OnReplaySaved;
            _output.Dispose();
            _videoEncoder.Dispose();
            _audioEncoder?.Dispose();
            _audioRouting?.Dispose();
        }
    }

    private sealed class CombinedRecorderOutput : IRecorderOutput, IReplayBufferOutput
    {
        private readonly MuxerOutput _session;
        private readonly MuxerOutput _replay;
        private readonly object _gate = new();
        private bool _sessionStopped;
        private bool _replayStopped;
        private bool _signalled;

        internal CombinedRecorderOutput(MuxerOutput session, MuxerOutput replay)
        {
            _session = session;
            _replay = replay;
            _session.Stopped += OnSessionStopped;
            _replay.Stopped += OnReplayStopped;
        }

        public bool IsActive => _session.IsActive || _replay.IsActive;

        public string? LastError => _session.LastError ?? _replay.LastError;

        public event EventHandler<ObsOutputStopEvent>? Stopped;

        public bool Start()
        {
            lock (_gate)
            {
                _sessionStopped = false;
                _replayStopped = false;
                _signalled = false;
            }

            if (!_session.Start())
                return false;
            if (_replay.Start())
                return true;

            _session.Stop();
            return false;
        }

        public void Stop()
        {
            if (_replay.IsActive)
                _replay.Stop();
            if (_session.IsActive)
                _session.Stop();
        }

        public bool WaitForStop(TimeSpan timeout) =>
            _session.WaitForStop(timeout) && _replay.WaitForStop(timeout);

        public bool SaveReplay(string directory, string format, Action<string> onSaved) =>
            _replay.SaveReplay(directory, format, onSaved);

        public bool WaitForReplaySave(TimeSpan timeout) => _replay.WaitForReplaySave(timeout);

        private void OnSessionStopped(object? sender, ObsOutputStopEvent stop)
        {
            lock (_gate)
                _sessionStopped = true;
            TrySignal(stop);
        }

        private void OnReplayStopped(object? sender, ObsOutputStopEvent stop)
        {
            lock (_gate)
                _replayStopped = true;
            TrySignal(stop);
        }

        private void TrySignal(ObsOutputStopEvent stop)
        {
            lock (_gate)
            {
                if (_signalled || !_sessionStopped || !_replayStopped)
                    return;
                _signalled = true;
            }

            Stopped?.Invoke(this, stop);
        }

        public void Dispose()
        {
            _session.Stopped -= OnSessionStopped;
            _replay.Stopped -= OnReplayStopped;
            _replay.Dispose();
            _session.Dispose();
        }
    }
}
