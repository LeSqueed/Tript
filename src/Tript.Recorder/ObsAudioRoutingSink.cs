// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;
using Tript.Settings;

namespace Tript.Recorder;

// The real IAudioRoutingSink: a thin wrapper over the binding's audio plumbing. Each call is the
// corresponding binding surface — obs_source_set_audio_mixers, obs_source_set_volume, the active
// pair, obs_audio_encoder_create with the mixer index, obs_output_set_audio_encoder with the slot.
// The wrapper types (RoutedSource, TrackEncoder) keep the binding objects alive while the routing
// is wired, and dispose their references when the routing is disposed.
public sealed class ObsAudioRoutingSink : IAudioRoutingSink
{
    // How a source kind maps to a concrete capture source type. Linux exposes the pulseaudio
    // captures; other platforms would map Input and Output to their own ids. Device enumeration is
    // deliberately a seam for the alpha (spec/recorder.md, "Multi-track audio"): the settings model
    // selects a source by name, and mapping that name to a device id is future work. The default
    // picks the pulseaudio types.
    private readonly ObsOutput _output;

    private readonly nint _audioHandle;

    private readonly Func<AudioSourceKind, string> _sourceTypeResolver;

    private readonly string _audioEncoderId;

    // The output the routing wires encoders into, and the audio mix the track encoders bind to. The
    // sink holds both because a routing is always for a particular output — the recorder creates the
    // sink with the output it owns, and the encoders must be bound to the mix libobs is actually
    // running or they encode nothing (libobs never binds them itself; obs_encoder_set_audio is the
    // caller's job — the existing harness does exactly this). The audio handle can be zero when no
    // real mix exists, which the unit tests' fake sink never needs.
    public ObsAudioRoutingSink(
        ObsOutput output,
        nint audioHandle,
        Func<AudioSourceKind, string>? sourceTypeResolver = null,
        string audioEncoderId = "ffmpeg_aac")
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        _audioHandle = audioHandle;
        _sourceTypeResolver = sourceTypeResolver ?? DefaultSourceTypeId;
        _audioEncoderId = audioEncoderId;
    }

    public IAudioRoutedSource CreateCaptureSource(AudioSourceKind kind, string name) =>
        new RoutedSource(ObsSource.CreatePrivate(_sourceTypeResolver(kind), $"audio:{name}"));

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

    private static string DefaultSourceTypeId(AudioSourceKind kind) => kind switch
    {
        AudioSourceKind.Input => "pulse_input_capture",
        AudioSourceKind.Output => "pulse_output_capture",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown audio source kind.")
    };

    private sealed class RoutedSource : IAudioRoutedSource, IDisposable
    {
        internal RoutedSource(ObsSource source) => Source = source;

        internal ObsSource Source { get; }

        public void Dispose() => Source.Dispose();
    }

    private sealed class TrackEncoder : IAudioTrackEncoder, IDisposable
    {
        internal TrackEncoder(ObsEncoder encoder) => Encoder = encoder;

        internal ObsEncoder Encoder { get; }

        public void Dispose() => Encoder.Dispose();
    }
}
