// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;

namespace Tript.Recorder;

// Turns the settings model's audio tracks into the wiring plan the routing service applies. This is
// the whole settings→binding mapping, kept pure so it is unit-testable without a live libobs
// context: given the resolved track list (from ResolvedRecorderSettings.AudioTracks), the plan says
// which mixer bit each source gets, which mixer each track's encoder draws from, and which output
// slot it is assigned to.
public static class AudioRoutingPlanner
{
    // The bound on tracks. libobs exposes MAX_AUDIO_MIXES mixers and MAX_OUTPUT_AUDIO_ENCODERS
    // output slots, both 6, and a track occupies one of each — so a plan can never carry more
    // tracks than 6.
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
