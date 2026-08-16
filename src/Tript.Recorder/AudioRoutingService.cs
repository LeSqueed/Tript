// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;

namespace Tript.Recorder;

// The audio routing service: takes the resolved audio tracks the recorder hands over (via
// ResolvedRecorderSettings.AudioTracks) and produces the wired audio path — capture sources routed
// into mixers, one encoder per track bound to its mixer and assigned to its output slot — plus the
// track/source mapping the recording's metadata must carry. The recorder owns the output; this owns
// the audio routing (spec/recorder.md, "Settings that reach the recorder").
//
// The component is deliberately separate from the recorder: the recorder's state machine never
// depends on how audio is wired, and the wiring is pure enough to be verified against a fake sink.
public sealed class AudioRoutingService
{
    private readonly IAudioRoutingSink _sink;

    public AudioRoutingService(IAudioRoutingSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
    }

    // Wires the whole audio path for a plan and returns the sources and encoders it created, plus
    // the metadata layout describing what went where. The recorder holds onto the returned wiring
    // for the life of the recording and disposes it when it stops. The output the encoders are
    // assigned to is the sink's own, given to it at construction.
    public AudioRouting Wire(AudioRoutingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var sources = new List<IAudioRoutedSource>(plan.TrackCount);
        var encoders = new List<IAudioTrackEncoder>(plan.TrackCount);

        foreach (var track in plan.Tracks)
        {
            foreach (var source in track.Sources)
            {
                var capture = _sink.CreateCaptureSource(source.Kind, source.Name);
                _sink.RouteSourceToMixer(capture, track.MixerIndex);
                _sink.SetSourceVolume(capture, source.Volume);
                _sink.ActivateSource(capture);
                sources.Add(capture);
            }

            var encoder = _sink.CreateTrackEncoder(track.MixerIndex, track.Name);
            _sink.AssignEncoderToSlot(encoder, track.MixerIndex);
            encoders.Add(encoder);
        }

        return new AudioRouting(_sink, sources, encoders, BuildMetadata(plan));
    }

    // The track/source mapping the recording's metadata contract carries
    // (spec/config-and-storage.md, "Recording metadata"): which track holds which source, at which
    // volume. A later stage reads this from the recording to reconstruct the layout after
    // compression.
    public static RecordingMetadata BuildMetadata(AudioRoutingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var metadata = new RecordingMetadata();
        foreach (var track in plan.Tracks)
        {
            metadata.AudioTracks.Add(new AudioTrackLayout
            {
                Index = track.MixerIndex,
                Name = track.Name,
                Sources = [.. track.Sources.Select(source => new SourceOnTrack
                {
                    Name = source.Name,
                    Volume = source.Volume
                })]
            });
        }

        return metadata;
    }
}
