// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;
using Xunit;

namespace Tript.Recorder.Tests;

// The settings→binding mapping, tested without a live libobs context: given the resolved audio
// tracks the recorder hands over, the plan says which mixer bit each source gets, which mixer each
// track's encoder draws from, and which output slot it lands in. A track = a mixer = an output
// slot (spec/obs-binding.md, "Audio routing and tracks"); two sources on one track must share the
// mixer bit; volume is per-source.
public sealed class AudioRoutingPlannerTests
{
    [Fact]
    public void TwoTracksTwoSources_MapToDistinctMixersBitsAndSlots()
    {
        var tracks = new List<AudioTrack>
        {
            new() { Name = "Game", Sources = { new AudioSource { Name = "Game audio", Kind = AudioSourceKind.Output, Volume = 0.8f } } },
            new() { Name = "Mic", Sources = { new AudioSource { Name = "Mic", Kind = AudioSourceKind.Input, Volume = 1.0f } } },
        };

        var plan = AudioRoutingPlanner.Plan(tracks);

        Assert.Equal(2, plan.TrackCount);
        var game = plan.Tracks[0];
        var mic = plan.Tracks[1];

        // Track 0 is mixer 0, bit 0 (0b001), output slot 0.
        Assert.Equal(0, game.MixerIndex);
        Assert.Equal(0b001u, game.MixerMask);
        Assert.Equal(0, game.MixerIndex); // the output slot is the same value
        Assert.Single(game.Sources);
        Assert.Equal("Game audio", game.Sources[0].Name);

        // Track 1 is mixer 1, bit 1 (0b010), output slot 1.
        Assert.Equal(1, mic.MixerIndex);
        Assert.Equal(0b010u, mic.MixerMask);
        Assert.Equal(1, mic.MixerIndex);
        Assert.Single(mic.Sources);
        Assert.Equal("Mic", mic.Sources[0].Name);
    }

    // The concrete routing the design session named: game and mic on separate tracks, Discord and
    // mic merged into a third track. Each source on a track shares that track's mixer bit, and the
    // same source (mic) appearing on two tracks gets routed to both bits — it is a single source
    // feeding two mixers.
    [Fact]
    public void TheSameSourceOnTwoTracks_IsRoutedToBothMixerBits()
    {
        var tracks = new List<AudioTrack>
        {
            new() { Name = "Game", Sources = { new AudioSource { Name = "Game audio", Kind = AudioSourceKind.Output, Volume = 1.0f } } },
            new() { Name = "Mic", Sources = { new AudioSource { Name = "Mic", Kind = AudioSourceKind.Input, Volume = 1.0f } } },
            new() { Name = "Discord + Mic", Sources =
            {
                new AudioSource { Name = "Discord", Kind = AudioSourceKind.Output, Volume = 0.5f },
                new AudioSource { Name = "Mic", Kind = AudioSourceKind.Input, Volume = 1.0f },
            } },
        };

        var plan = AudioRoutingPlanner.Plan(tracks);

        Assert.Equal(3, plan.TrackCount);
        Assert.Equal(0b001u, plan.Tracks[0].MixerMask);
        Assert.Equal(0b010u, plan.Tracks[1].MixerMask);
        Assert.Equal(0b100u, plan.Tracks[2].MixerMask);

        // Both sources on the merged track carry the same mixer bit.
        var merged = plan.Tracks[2];
        Assert.Equal(2, merged.Sources.Count);
        Assert.Equal(0b100u, merged.MixerMask);
    }

    [Fact]
    public void TwoSourcesOnOneTrack_ShareTheMixerBit()
    {
        var tracks = new List<AudioTrack>
        {
            new() { Name = "Merged", Sources =
            {
                new AudioSource { Name = "Discord", Kind = AudioSourceKind.Output, Volume = 0.5f },
                new AudioSource { Name = "Mic", Kind = AudioSourceKind.Input, Volume = 1.0f },
            } },
        };

        var plan = AudioRoutingPlanner.Plan(tracks);

        var track = Assert.Single(plan.Tracks);
        Assert.Equal(2, track.Sources.Count);
        // A track has one mixer bit, shared by every source merged into it.
        Assert.Equal(0b001u, track.MixerMask);
        Assert.All(track.Sources, source => Assert.Equal(0, track.MixerIndex));
    }

    [Fact]
    public void PerSourceVolume_IsCarriedThroughThePlan()
    {
        var tracks = new List<AudioTrack>
        {
            new() { Name = "Game", Sources =
            {
                new AudioSource { Name = "Game audio", Kind = AudioSourceKind.Output, Volume = 0.4f },
                new AudioSource { Name = "Discord", Kind = AudioSourceKind.Output, Volume = 0.75f },
            } },
        };

        var plan = AudioRoutingPlanner.Plan(tracks);

        var track = Assert.Single(plan.Tracks);
        Assert.Equal(0.4f, track.Sources[0].Volume);
        Assert.Equal(0.75f, track.Sources[1].Volume);
    }

    [Fact]
    public void ZeroTracks_ProducesAnEmptyPlan()
    {
        var plan = AudioRoutingPlanner.Plan([]);

        Assert.Equal(0, plan.TrackCount);
        Assert.Empty(plan.Tracks);
    }

    // A source that is configured but not attached to any track has nowhere to be routed — the plan
    // only carries sources that sit in a track's Sources list, so the unassigned source produces no
    // capture source and no mixer bit. A track with an empty source list is a track that records
    // silence; it still gets a mixer, an encoder and an output slot.
    [Fact]
    public void AnUnassignedSource_IsNotCarriedInThePlan_AndAnEmptyTrackStillGetsAMixer()
    {
        var tracks = new List<AudioTrack>
        {
            new() { Name = "Silent", Sources = { } },
        };
        // A source that exists but was never added to a track's Sources:
        _ = new AudioSource { Name = "Unassigned", Kind = AudioSourceKind.Input, Volume = 1.0f };

        var plan = AudioRoutingPlanner.Plan(tracks);

        var silent = Assert.Single(plan.Tracks);
        Assert.Empty(silent.Sources);
        // The empty track still occupies a mixer bit, an encoder mixer and an output slot.
        Assert.Equal(0, silent.MixerIndex);
        Assert.Equal(0b001u, silent.MixerMask);
        Assert.Single(plan.Tracks);
    }

    // More tracks than MAX_AUDIO_MIXES / MAX_OUTPUT_AUDIO_ENCODERS (both 6) cannot be expressed:
    // there are no mixers or output slots left. Reject rather than clamp, so a misconfiguration is
    // loud instead of silently dropping a track.
    [Fact]
    public void MoreThanSixTracks_IsRejected()
    {
        var tracks = Enumerable.Range(0, 7)
            .Select(i => new AudioTrack { Name = $"Track {i}" })
            .ToList();

        var failure = Assert.Throws<ArgumentOutOfRangeException>(() => AudioRoutingPlanner.Plan(tracks));

        Assert.Contains("6", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SixTracks_IsTheAcceptedMaximum()
    {
        var tracks = Enumerable.Range(0, 6)
            .Select(i => new AudioTrack { Name = $"Track {i}" })
            .ToList();

        var plan = AudioRoutingPlanner.Plan(tracks);

        Assert.Equal(6, plan.TrackCount);
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(i, plan.Tracks[i].MixerIndex);
            Assert.Equal(1u << i, plan.Tracks[i].MixerMask);
        }
    }

    [Fact]
    public void NullTracks_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => AudioRoutingPlanner.Plan(null!));
    }
}
