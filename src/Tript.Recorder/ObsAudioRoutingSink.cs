// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;
using Tript.Settings;

namespace Tript.Recorder;

public sealed class ObsAudioRoutingSink : IAudioRoutingSink
{
    private readonly ObsOutput _output;

    private readonly nint _audioHandle;

    private readonly Func<AudioSourceKind, string> _sourceTypeResolver;

    private readonly string _audioEncoderId;

    private readonly ObsScene? _scene;

    private const string DeviceIdKey = "device_id";

    public ObsAudioRoutingSink(
        ObsOutput output,
        nint audioHandle,
        Func<AudioSourceKind, string>? sourceTypeResolver = null,
        string audioEncoderId = "ffmpeg_aac",
        ObsScene? scene = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        _audioHandle = audioHandle;
        _sourceTypeResolver = sourceTypeResolver ?? DefaultSourceTypeId;
        _audioEncoderId = audioEncoderId;
        _scene = scene;
    }

    public IAudioRoutedSource CreateCaptureSource(AudioSourceKind kind, string name, string? deviceId)
    {
        var source = CreatePrivateCaptureSource(_sourceTypeResolver(kind), $"audio:{name}", deviceId);

        try
        {
            return new RoutedSource(source, _scene?.AddSource(source));
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    internal static ObsSource CreatePrivateCaptureSource(string sourceType, string name, string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
            return ObsSource.CreatePrivate(sourceType, name);

        using var settings = new ObsSettings();
        settings.SetString(DeviceIdKey, deviceId);
        return ObsSource.CreatePrivate(sourceType, name, settings);
    }

    public void RouteSourceToMixer(IAudioRoutedSource source, int mixerIndex) =>
        ((RoutedSource)source).Source.AudioMixers = 1u << mixerIndex;

    public void SetSourceVolume(IAudioRoutedSource source, float volume) =>
        ((RoutedSource)source).Source.Volume = volume;

    public void ActivateSource(IAudioRoutedSource source) => ((RoutedSource)source).Source.MarkActive();

    public void DeactivateSource(IAudioRoutedSource source) => ((RoutedSource)source).Source.MarkInactive();

    public IAudioTrackEncoder CreateTrackEncoder(int mixerIndex, string name)
    {
        var encoder = ObsEncoder.CreateAudio(_audioEncoderId, $"audio track: {name}", mixerIndex: (nuint)mixerIndex);
        if (_audioHandle != nint.Zero)
            encoder.BindToAudio(_audioHandle);
        return new TrackEncoder(encoder);
    }

    public void AssignEncoderToSlot(IAudioTrackEncoder encoder, int outputSlot) =>
        _output.SetAudioEncoder(((TrackEncoder)encoder).Encoder, (nuint)outputSlot);

    internal static string DefaultSourceTypeId(AudioSourceKind kind) =>
        OperatingSystem.IsWindows() ? WasapiSourceTypeId(kind) : PulseAudioSourceTypeId(kind);

    internal static string PulseAudioSourceTypeId(AudioSourceKind kind) => kind switch
    {
        AudioSourceKind.Input => "pulse_input_capture",
        AudioSourceKind.Output => "pulse_output_capture",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown audio source kind.")
    };

    internal static string WasapiSourceTypeId(AudioSourceKind kind) => kind switch
    {
        AudioSourceKind.Input => "wasapi_input_capture",
        AudioSourceKind.Output => "wasapi_output_capture",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown audio source kind.")
    };

    private sealed class RoutedSource : IAudioRoutedSource, IDisposable
    {
        private readonly ObsSceneItem? _item;

        internal RoutedSource(ObsSource source, ObsSceneItem? item)
        {
            Source = source;
            _item = item;
        }

        internal ObsSource Source { get; }

        public void Dispose()
        {
            if (_item is not null)
            {
                _item.Remove();
                _item.Dispose();
            }

            Source.Dispose();
        }
    }

    private sealed class TrackEncoder : IAudioTrackEncoder, IDisposable
    {
        internal TrackEncoder(ObsEncoder encoder) => Encoder = encoder;

        internal ObsEncoder Encoder { get; }

        public void Dispose() => Encoder.Dispose();
    }
}
