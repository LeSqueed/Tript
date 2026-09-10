// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;

namespace Tript.Recorder;

public static class AudioRoutingPlanner
{
    public const int MaxAudioTracks = 6;

    public static AudioRoutingPlan Plan(IReadOnlyList<AudioTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        if (tracks.Count > MaxAudioTracks)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tracks),
                $"A recording supports at most {MaxAudioTracks} audio tracks; {tracks.Count} were configured.");
        }

        var planned = new List<PlannedAudioTrack>(tracks.Count);
        for (var index = 0; index < tracks.Count; index++)
        {
            var track = tracks[index];
            planned.Add(new PlannedAudioTrack
            {
                Name = track.Name,
                MixerIndex = index,
                Sources = [.. track.Sources.Select(source => new PlannedAudioSource
                {
                    Name = source.Name,
                    Kind = source.Kind,
                    Volume = source.Volume,
                    DeviceId = source.DeviceId
                })]
            });
        }

        return new AudioRoutingPlan { Tracks = planned };
    }
}
