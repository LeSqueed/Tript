// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;

namespace Tript.Recorder;

// An audio capture source the routing created. Opaque on purpose: the routing service only routes
// it into a mixer, sets its volume and marks it active; it never inspects it. The concrete source
// is the sink's business (ObsAudioRoutingSink wraps the binding's ObsSource).
public interface IAudioRoutedSource
{
}

// An audio encoder the routing created, one per track. Opaque for the same reason; the sink wraps
// the binding's ObsEncoder.
public interface IAudioTrackEncoder
{
}

// The seam between the routing service and the binding's audio plumbing. The service decides the
// wiring — which source goes to which mixer, which mixer each track's encoder draws from, which
// output slot it lands in — and asks the sink to make it so.
public interface IAudioRoutingSink
{
    // Creates the capture source for one routed source. How a source kind maps to a concrete source
    // type — and how a deviceId picks a device — is the sink's business.
    IAudioRoutedSource CreateCaptureSource(AudioSourceKind kind, string name, string? deviceId);

    // Routes a source's audio to the mixer that feeds the given track: obs_source_set_audio_mixers
    // with the track's bit. Two sources on the same track must receive the same mixer index.
    void RouteSourceToMixer(IAudioRoutedSource source, int mixerIndex);

    // Applies a source's per-source volume (a linear gain).
    void SetSourceVolume(IAudioRoutedSource source, float volume);

    // Marks a source active so it actually produces audio, and removes that mark.
    void ActivateSource(IAudioRoutedSource source);

    void DeactivateSource(IAudioRoutedSource source);

    // Creates an audio encoder bound to the given mixer — the mixer-to-encoder joint. The mixer
    // index is fixed for the encoder's life.
    IAudioTrackEncoder CreateTrackEncoder(int mixerIndex, string name);

    // Assigns an encoder to the output's audio track slot — the encoder-to-output-slot joint. Track
    // n in the resulting file corresponds to output slot n, which draws mixer n. The output is the
    // sink's: it was handed one at construction, because a routing wires a particular output.
    void AssignEncoderToSlot(IAudioTrackEncoder encoder, int outputSlot);
}
