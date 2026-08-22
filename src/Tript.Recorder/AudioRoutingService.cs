// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;

namespace Tript.Recorder;

// The audio routing service: takes the resolved audio tracks the recorder hands over (via
// ResolvedRecorderSettings.AudioTracks) and produces the wired audio path — capture sources routed
// into mixers, one encoder per track bound to its mixer and assigned to its output slot — plus the
// track/source mapping the recording's metadata must carry. The recorder owns the output; this owns
// the audio routing.
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
    // for the life of the recording and disposes it when it stops.
    public AudioRouting Wire(AudioRoutingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var sources = new List<IAudioRoutedSource>(plan.TrackCount);
        var activeSources = new List<IAudioRoutedSource>(plan.TrackCount);
        var encoders = new List<IAudioTrackEncoder>(plan.TrackCount);

        try
        {
            foreach (var track in plan.Tracks)
            {
                foreach (var source in track.Sources)
                {
                    var capture = _sink.CreateCaptureSource(source.Kind, source.Name, source.DeviceId);
                    sources.Add(capture);
                    _sink.RouteSourceToMixer(capture, track.MixerIndex);
                    _sink.SetSourceVolume(capture, source.Volume);
                    _sink.ActivateSource(capture);
                    activeSources.Add(capture);
                }

                var encoder = _sink.CreateTrackEncoder(track.MixerIndex, track.Name);
                encoders.Add(encoder);
                _sink.AssignEncoderToSlot(encoder, track.MixerIndex);
            }
        }
        catch
        {
            for (var i = activeSources.Count - 1; i >= 0; i--)
            {
                try
                {
                    _sink.DeactivateSource(activeSources[i]);
                }
                catch
                {
                }
            }

            for (var i = encoders.Count - 1; i >= 0; i--)
            {
                if (encoders[i] is IDisposable disposable)
                {
                    try
                    {
                        disposable.Dispose();
                    }
                    catch
                    {
                    }
                }
            }

            for (var i = sources.Count - 1; i >= 0; i--)
            {
                if (sources[i] is IDisposable disposable)
                {
                    try
                    {
                        disposable.Dispose();
                    }
                    catch
                    {
                    }
                }
            }

            throw;
        }

        return new AudioRouting(_sink, sources, encoders, BuildMetadata(plan));
    }

    // The track/source mapping the recording's metadata contract carries: which track holds which
    // source, at which volume. A later stage reads this from the recording to reconstruct the
    // layout after compression.
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
