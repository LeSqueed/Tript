// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;
using Xunit;

namespace Tript.Recorder.Tests;

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

        Assert.Equal(0, game.MixerIndex);
        Assert.Equal(0b001u, game.MixerMask);
        Assert.Equal(0, game.MixerIndex);
        Assert.Single(game.Sources);
        Assert.Equal("Game audio", game.Sources[0].Name);

        Assert.Equal(1, mic.MixerIndex);
        Assert.Equal(0b010u, mic.MixerMask);
        Assert.Equal(1, mic.MixerIndex);
        Assert.Single(mic.Sources);
        Assert.Equal("Mic", mic.Sources[0].Name);
    }

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
    public void PerSourceDeviceId_IsCarriedThroughThePlan()
    {
        const string micId = "\\\\?\\SWD\\MMDEVAPI\\{0.0.1.00000000}.{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}";
        var tracks = new List<AudioTrack>
        {
            new() { Name = "Game", Sources =
            {
                new AudioSource { Name = "Game audio", Kind = AudioSourceKind.Output, Volume = 1.0f },
                new AudioSource { Name = "Mic", Kind = AudioSourceKind.Input, Volume = 1.0f, DeviceId = micId },
            } },
        };

        var plan = AudioRoutingPlanner.Plan(tracks);

        var track = Assert.Single(plan.Tracks);
        Assert.Null(track.Sources[0].DeviceId);
        Assert.Equal(micId, track.Sources[1].DeviceId);
    }

    [Fact]
    public void ZeroTracks_ProducesAnEmptyPlan()
    {
        var plan = AudioRoutingPlanner.Plan([]);

        Assert.Equal(0, plan.TrackCount);
        Assert.Empty(plan.Tracks);
    }

    [Fact]
    public void AnUnassignedSource_IsNotCarriedInThePlan_AndAnEmptyTrackStillGetsAMixer()
    {
        var tracks = new List<AudioTrack>
        {
            new() { Name = "Silent", Sources = { } },
        };

        _ = new AudioSource { Name = "Unassigned", Kind = AudioSourceKind.Input, Volume = 1.0f };

        var plan = AudioRoutingPlanner.Plan(tracks);

        var silent = Assert.Single(plan.Tracks);
        Assert.Empty(silent.Sources);

        Assert.Equal(0, silent.MixerIndex);
        Assert.Equal(0b001u, silent.MixerMask);
        Assert.Single(plan.Tracks);
    }

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
