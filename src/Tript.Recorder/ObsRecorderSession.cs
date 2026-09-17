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

    private const string FfmpegAacId = "ffmpeg_aac";
    private const uint VideoChannel = 0;

    private const int KeyframeIntervalSeconds = 1;

    private readonly ObsSource _source;
    private readonly ObsScene _scene;
    private readonly ObsSource? _displaySource;
    private readonly ObsSource? _gameCaptureSource;
    private readonly ObsSceneItem _colourItem;
    private readonly ObsSceneItem? _displayItem;
    private readonly ObsSceneItem? _gameItem;

    private readonly GameCaptureHookProbe _hookProbe;

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
                _displaySource = CaptureSourceFactory.CreateDisplay(Policy.PreferredDisplayId, out var display);
                SelectedDisplay = display;
                if (_displaySource is not null)
                {
                    _displayItem = _scene.AddSource(_displaySource)
                        ?? throw new ObsException("The recorder scene refused the display-capture source.");
                }
            }

            if (Policy.IncludesGameCapture)
            {
                _gameCaptureSource = CaptureSourceFactory.CreateGame(gameCaptureTarget);
                if (_gameCaptureSource is not null)
                {
                    _gameItem = _scene.AddSource(_gameCaptureSource)
                        ?? throw new ObsException("The recorder scene refused the game-capture source.");
                }
            }

            _hookProbe = new GameCaptureHookProbe(_gameCaptureSource, Policy, _displayItem);
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

    public bool IsGameCaptureHooked => _hookProbe.IsHooked;

    public bool HasDisplayFallback => _displaySource is not null;

    public IRecorderOutput CreateOutput(ResolvedRecorderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var outputIds = settings.Mode switch
        {
            RecordingMode.ReplayBufferOnly => new[] { ReplayBufferId },
            _ when settings.Mode.UsesReplayBuffer() => [FfmpegMuxerId, ReplayBufferId],
            _ => [FfmpegMuxerId]
        };

        foreach (var outputId in outputIds)
        {
            if (!ObsOutput.IsTypeRegistered(outputId))
                throw new ObsException($"No loaded module registers the output type '{outputId}'.");
        }

        if (!ObsEncoder.IsTypeRegistered(FfmpegAacId))
            throw new ObsException($"No loaded module registers the audio encoder '{FfmpegAacId}'.");

        var plan = RecorderColourPolicy.Resolve(Runtime, settings);
        plan = RecorderColourPolicy.ApplyToCanvas(Runtime, plan, PlaceSourceOnChannel, ApplyCaptureColour);
        ApplyCaptureColour(plan);

        if (!Runtime.TryGetVideoHandle(out var video))
            throw new ObsException("The runtime has no video mix; obs_reset_video must succeed before recording.");

        if (!Runtime.TryGetAudioHandle(out var audio))
            throw new ObsException("The runtime has no audio mix; obs_reset_audio must succeed before recording.");

        SizeColourSourceToCanvas(settings.ResolutionWidth, settings.ResolutionHeight);
        FitItemsToCanvas(settings.ResolutionWidth, settings.ResolutionHeight);

        var videoEncoder = CreateVideoEncoder(settings, plan, video);
        MuxerOutput? session = null;
        try
        {
            if (settings.Mode is RecordingMode.ReplayBufferOnly)
            {
                return CreateMuxerOutput(settings, ReplayBufferId, "replay buffer", audio, videoEncoder,
                    ownsVideoEncoder: true);
            }

            session = CreateMuxerOutput(settings, FfmpegMuxerId, "recorder output", audio, videoEncoder,
                ownsVideoEncoder: true);
            if (!settings.Mode.UsesReplayBuffer())
                return session;

            var replay = CreateMuxerOutput(settings, ReplayBufferId, "replay buffer", audio, videoEncoder,
                ownsVideoEncoder: false, includeCaptureSources: false);
            return new CombinedRecorderOutput(session, replay);
        }
        catch
        {
            if (session is not null)
                session.Dispose();
            else
                videoEncoder.Dispose();
            throw;
        }
    }

    private static ObsEncoder CreateVideoEncoder(ResolvedRecorderSettings settings, HdrPlan plan, nint video)
    {
        var rateControl = ObsEncoderPolicy.ResolveRateControl(plan.EncoderId, settings.RateControl);
        using var videoSettings = new ObsSettings();
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

        var videoEncoder = ObsEncoder.CreateVideo(plan.EncoderId, "recorder video", videoSettings);
        try
        {
            videoEncoder.BindToVideo(video);
            videoEncoder.SetScaledSize((uint)settings.ResolutionWidth, (uint)settings.ResolutionHeight);
            return videoEncoder;
        }
        catch
        {
            videoEncoder.Dispose();
            throw;
        }
    }

    private MuxerOutput CreateMuxerOutput(ResolvedRecorderSettings settings, string outputId, string outputName,
        nint audio, ObsEncoder videoEncoder, bool ownsVideoEncoder, bool includeCaptureSources = true)
    {
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

        ObsEncoder? audioEncoder = null;
        AudioRouting? audioRouting = null;

        try
        {
            output.SetVideoEncoder(videoEncoder);

            if (settings.AudioTracks.Count > 0)
            {
                var sink = new ObsAudioRoutingSink(output, audio, audioEncoderId: FfmpegAacId, scene: _scene);
                audioRouting = new AudioRoutingService(sink).Wire(
                    AudioRoutingPlanner.Plan(settings.AudioTracks), includeCaptureSources);
            }
            else
            {
                using var audioSettings = new ObsSettings();
                audioSettings.SetInt("bitrate", 160);
                audioEncoder = ObsEncoder.CreateAudio(FfmpegAacId, "recorder audio", audioSettings, mixerIndex: 0);
                audioEncoder.BindToAudio(audio);
                output.SetAudioEncoder(audioEncoder, 0);
            }

            return new MuxerOutput(output, ownsVideoEncoder ? videoEncoder : null, audioEncoder, audioRouting,
                outputId == ReplayBufferId);
        }
        catch
        {
            output.Dispose();
            audioEncoder?.Dispose();
            audioRouting?.Dispose();
            throw;
        }
    }

    public void PlaceSourceOnChannel()
    {
        Runtime.SetOutputSource(VideoChannel, _scene);
        _hookProbe.Start();
    }

    public void ClearSourceFromChannel()
    {
        _hookProbe.Stop();
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
        _hookProbe.Dispose();
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

    public bool HasGameCaptureSource => _gameCaptureSource is not null;

    public bool WaitForGameCapture(TimeSpan deadline, TimeSpan warningAfter, Action showWarning,
        Action clearWarning, CancellationToken cancellationToken) =>
        _hookProbe.WaitForHook(deadline, warningAfter, showWarning, clearWarning, cancellationToken);

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

    private void ApplyCaptureColour(HdrPlan plan)
    {
        RecorderColourPolicy.ApplyForceSdr(_gameCaptureSource, plan.ForceSdrOnCapture);
        RecorderColourPolicy.ApplyForceSdr(_displaySource, plan.ForceSdrOnCapture);
    }

}
