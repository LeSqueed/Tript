// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Numerics;
using Serilog;
using Tript.Obs;
using Tript.Settings;

namespace Tript.Recorder;

public sealed class ObsRecorderSession : IRecorderSession
{
    private const string FfmpegMuxerId = "ffmpeg_muxer";
    private const string ReplayBufferId = "replay_buffer";
    private const string GameCaptureId = "game_capture";

    private const string FfmpegAacId = "ffmpeg_aac";
    private const uint VideoChannel = 0;

    private const int KeyframeIntervalSeconds = 1;

    private static readonly TimeSpan HookProbeInterval = TimeSpan.FromSeconds(2);

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

    public CapturePolicy Policy { get; }

    public ObsDisplay? SelectedDisplay { get; }

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

    public bool HasDisplayFallback => _displaySource is not null;

    public IRecorderOutput CreateOutput(ResolvedRecorderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Mode is RecordingMode.ReplayBufferOnly)
            return CreateEncodedOutput(settings, ReplayBufferId, "replay buffer");

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

        ObsEncoder? videoEncoder = null;
        ObsEncoder? audioEncoder = null;
        AudioRouting? audioRouting = null;

        try
        {
            var rateControl = ObsEncoderPolicy.ResolveRateControl(videoEncoderId, settings.RateControl);
            using (var videoSettings = new ObsSettings())
            {
                videoSettings.SetString("rate_control", rateControl.Mode);

                if (rateControl.QuantiserKey is { } quantiserKey)
                    videoSettings.SetInt(quantiserKey, ObsEncoderPolicy.MapQualityToQuantiser(settings.Quality));

                if (rateControl.BitrateKey is { } bitrateKey)
                    videoSettings.SetInt(bitrateKey, ObsEncoderPolicy.ClampBitrateKbps(settings.BitrateKbps));

                if (rateControl.MaxBitrateKey is { } maxBitrateKey)
                    videoSettings.SetInt(maxBitrateKey,
                        ObsEncoderPolicy.ResolveMaxBitrateKbps(settings.BitrateKbps, settings.MaxBitrateKbps));

                videoSettings.SetInt("keyint_sec", KeyframeIntervalSeconds);

                if (plan.Profile is { } profile)
                    videoSettings.SetString("profile", profile);

                videoEncoder = ObsEncoder.CreateVideo(videoEncoderId, "recorder video", videoSettings);
                videoEncoder.BindToVideo(video);
                videoEncoder.SetScaledSize((uint)settings.ResolutionWidth, (uint)settings.ResolutionHeight);
                output.SetVideoEncoder(videoEncoder);
            }

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

    public bool RetargetGame(ObsGameCaptureTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (_gameCaptureSource is null || !ObsCaptureSource.Retarget(_gameCaptureSource, target))
            return false;

        Log.Information("ObsRecorderSession: game capture re-targeted at {Window}",
            ObsCaptureSource.BuildWindowMatchString(target));
        return true;
    }

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

    private static ObsSource? CreateGameCaptureSource(ObsGameCaptureTarget? target)
    {
        var properties = ObsSourceProperties.EnumerateTypeProperties(GameCaptureId);
        if (properties.Count == 0)
            return null;

        using var settings = target is not null
            ? ObsCaptureSource.BuildGameCaptureSettings(target) ?? new ObsSettings()
            : new ObsSettings();

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

        settings.SetInt(HookRateKey, FastestHookRate);

        return ObsSource.CreatePrivate(GameCaptureId, "app capture", settings);
    }

    private const string CaptureModeKey = "capture_mode";
    private const string WindowCaptureModeValue = "window";
    private const string HookRateKey = "hook_rate";
    private const long FastestHookRate = 3;

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

    private void StartHookProbe()
    {
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

    public bool WaitForGameCapture(TimeSpan deadline, TimeSpan warningAfter, Action showWarning,
        Action clearWarning, CancellationToken cancellationToken)
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

            var elapsed = DateTime.UtcNow - started;
            if (warningAfter > TimeSpan.Zero && !warningShown && elapsed >= warningAfter)
            {
                warningShown = true;
                showWarning();
            }

            if (elapsed >= deadline)
                return false;

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

    internal static TimeSpan HookDeadlineFor(CapturePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.Method == DisplayCaptureMethod.Game ? policy.GameCaptureTimeout : HookTimeout;
    }

    private void SizeColourSourceToCanvas(int width, int height)
    {
        using var settings = _source.GetSettings();
        if (settings.GetInt("width") >= width && settings.GetInt("height") >= height)
            return;

        settings.SetInt("width", width);
        settings.SetInt("height", height);
        _source.Update(settings);
    }

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

    private HdrPlan ResolveHdrPlan(ResolvedRecorderSettings settings)
    {
        var candidates = ObsEncoderPolicy.EnumerateVideoEncoderCandidates();
        if (candidates.Count == 0)
            throw new ObsException("No loaded module registers a usable video encoder.");

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

    private ObsSourceColorSpace? CaptureColourSpace()
    {
        var probes = Runtime.ProbeDisplays();
        if (probes.Count == 0)
            return null;

        if (Log.IsEnabled(Serilog.Events.LogEventLevel.Debug))
            Log.Debug("ObsRecorderSession: display probe — {Probes}", string.Join("; ",
                probes.Select(p => $"[{p.MonitorIndex}] {p.ColorSpace} @ {p.SdrWhiteLevelNits} nits")));

        var choice = DisplayColourResolver.Choose(probes);

        if (choice.SdrWhiteLevelNits > 0f)
            Runtime.SetVideoLevels(choice.SdrWhiteLevelNits, ObsRuntime.DefaultHdrNominalPeakLevelNits);

        return choice.ColourSpace;
    }

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
            PlaceSourceOnChannel();
            return plan;
        }

        if (!plan.UseHdr)
        {
            Log.Warning("ObsRecorderSession: obs_reset_video refused the SDR canvas ({Result}); " +
                        "the mix keeps its current colour space.", result);
            return plan;
        }

        Log.Warning("ObsRecorderSession: obs_reset_video refused the HDR canvas ({Result}); " +
                    "recording SDR instead.", result);

        var downgraded = HdrPlanner.Decide(
            capturedColorSpace: ObsSourceColorSpace.Srgb,
            displayIsHdr: false,
            hdrEnabledInSettings: false,
            ObsEncoderPolicy.EnumerateVideoEncoderCandidates(),
            configuredEncoderId: null);

        ApplyCaptureColour(downgraded);
        return downgraded;
    }

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
            Log.Debug(exception, "ObsRecorderSession: could not set '{Key}' on a capture source.", ForceSdrKey);
        }
    }

    private const string ForceSdrKey = "force_sdr";

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
