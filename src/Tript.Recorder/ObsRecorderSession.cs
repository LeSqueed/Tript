// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;
using Tript.Settings;

namespace Tript.Recorder;

// The concrete recorder session over the OBS binding: an existing runtime plus a source the app
// owns. Building a session output is exactly the Layer 5 wiring — an ffmpeg_muxer output, a video
// encoder scaled to the resolved resolution, an audio encoder on mixer zero, both bound to the
// session's mixes. The output is the recorder's to own; the source is borrowed.
public sealed class ObsRecorderSession : IRecorderSession
{

    private const string FfmpegMuxerId = "ffmpeg_muxer";
    private const string X264Id = "obs_x264";
    private const string FfmpegAacId = "ffmpeg_aac";
    private const uint VideoChannel = 0;

    private readonly ObsSource _source;

    public ObsRecorderSession(ObsRuntime runtime, ObsSource source)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(source);

        Runtime = runtime;
        _source = source.AddReference();
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

        var videoEncoderId = ResolveVideoEncoderId();
        if (videoEncoderId is null)
            throw new ObsException("No loaded module registers a usable H.264 video encoder.");

        if (!ObsEncoder.IsTypeRegistered(FfmpegAacId))
            throw new ObsException($"No loaded module registers the audio encoder '{FfmpegAacId}'.");

        var outputSettings = new ObsSettings();
        outputSettings.SetString("path", settings.OutputPath);

        var output = ObsOutput.Create(FfmpegMuxerId, "recorder output", outputSettings);

        // The video encoder carries the resolved resolution and frame rate; libobs scales the mix
        // to the requested size. The encoder id resolved above decides which key set is written —
        // the x264 key set for the alpha (spec/obs-binding Part 10).
        using (var videoSettings = new ObsSettings())
        {
            videoSettings.SetString("rate_control", "CRF");
            videoSettings.SetInt("crf", MapQualityToCrf(settings.Quality));

            // keyint_sec is seconds, and the encoder converts to frames itself. A keyframe every
            // second keeps the file seekable; clamping to the range the x264 table declares.
            videoSettings.SetInt("keyint_sec", Math.Clamp(settings.Fps, 1, 10));

            var videoEncoder = ObsEncoder.CreateVideo(videoEncoderId, "recorder video", videoSettings);
            videoEncoder.BindToVideo(video);
            videoEncoder.SetScaledSize((uint)settings.ResolutionWidth, (uint)settings.ResolutionHeight);
            output.SetVideoEncoder(videoEncoder);
        }

        // A single audio encoder on mixer zero (the programme mix) in output slot zero, exactly the
        // Layer 5 shape that proved the audio track lands in the file.
        using (var audioSettings = new ObsSettings())
        {
            audioSettings.SetInt("bitrate", 160);
            var audioEncoder = ObsEncoder.CreateAudio(FfmpegAacId, "recorder audio", audioSettings, mixerIndex: 0);
            audioEncoder.BindToAudio(audio);
            output.SetAudioEncoder(audioEncoder, 0);
        }

        return new MuxerOutput(output);
    }

    public void PlaceSourceOnChannel() => Runtime.SetOutputSource(VideoChannel, _source);

    public void ClearSourceFromChannel() => Runtime.SetOutputSource(VideoChannel, (ObsSource?)null);

    public void Dispose() => _source.Dispose();

    // Which H.264 video encoder the runtime actually registered. The ids differ by machine and
    // runtime — obs_x264 on a software-capable install, the texture-NVENC ids on a machine with the
    // plugin present. The preference is the software x264 id, which is the alpha machine's; a
    // registered hardware id is used when the software one is absent.
    private static string? ResolveVideoEncoderId()
    {
        if (ObsEncoder.IsTypeRegistered(X264Id))
            return X264Id;

        return ObsEncoder.EnumerateTypeIds()
            .FirstOrDefault(id =>
                ObsEncoder.GetTypeCodec(id) is { } codec &&
                codec.Equals("h264", StringComparison.OrdinalIgnoreCase) &&
                ObsEncoder.GetType(id) == ObsEncoderType.Video);
    }

    // The quality profile in the resolved settings is the app's own 1..20 scale; x264's CRF runs
    // 0..51 with lower meaning better. The mapping is a straight inversion, clamped to the range
    // x264 accepts.
    internal static int MapQualityToCrf(int quality)
    {
        var clamped = Math.Clamp(quality, 1, 20);
        return (int)Math.Round(23 + (20 - clamped) * 1.0);
    }

    // The IRecorderOutput over a real ObsOutput: forwards start, stop and the stop signal, and owns
    // the output's lifetime. The stop signal is marshalled the same way ObsOutput does it — onto
    // the thread that subscribed — so the recorder's own marshalling stays as-is.
    private sealed class MuxerOutput : IRecorderOutput
    {
        private readonly ObsOutput _output;

        internal MuxerOutput(ObsOutput output)
        {
            _output = output;
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

        public void Dispose() => _output.Dispose();
    }
}
