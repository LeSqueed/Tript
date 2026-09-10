// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;

namespace Tript.Recorder;

public sealed class AudioRoutingService
{
    private readonly IAudioRoutingSink _sink;

    public AudioRoutingService(IAudioRoutingSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
    }

    public AudioRouting Wire(AudioRoutingPlan plan, bool includeCaptureSources = true)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var sources = new List<IAudioRoutedSource>(plan.TrackCount);
        var activeSources = new List<IAudioRoutedSource>(plan.TrackCount);
        var encoders = new List<IAudioTrackEncoder>(plan.TrackCount);

        try
        {
            foreach (var track in plan.Tracks)
            {
                if (includeCaptureSources)
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
