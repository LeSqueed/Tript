// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;
using Xunit;

namespace Tript.Recorder.Tests;

// The wiring the service performs through its sink, tested against a fake so no live libobs context
// is needed: each source is created, routed to its track's mixer bit, given its own volume, and
// marked active; each track gets one encoder bound to that mixer and assigned to that output slot;
// and the metadata layout records which track holds which source. The fake sink records the calls so
// a wrong mixer index, a missed volume or a missed slot each fails a distinct assertion.
public sealed class AudioRoutingServiceTests
{
    [Fact]
    public void TheService_CreatesEachSourceRoutesItToItsTracksMixerAndAppliesItsVolume()
    {
        var sink = new FakeSink();
        var service = new AudioRoutingService(sink);
        var plan = AudioRoutingPlanner.Plan(new List<AudioTrack>
        {
            new() { Name = "Game", Sources =
            {
                new AudioSource { Name = "Game audio", Kind = AudioSourceKind.Output, Volume = 0.4f },
                new AudioSource { Name = "Discord", Kind = AudioSourceKind.Output, Volume = 0.75f },
            } },
            new() { Name = "Mic", Sources = { new AudioSource { Name = "Mic", Kind = AudioSourceKind.Input, Volume = 1.0f } } },
        });

        using var routing = service.Wire(plan);

        Assert.Equal(3, sink.CreatedSources.Count);
        Assert.Equal(3, sink.Routed.Count);
        Assert.Equal(3, sink.Volumes.Count);
        Assert.Equal(3, sink.Activated.Count);

        // Both sources on track 0 are routed to mixer 0; the mic on track 1 to mixer 1.
        Assert.Equal([0, 0, 1], sink.Routed.Select(r => r.MixerIndex));
        // Volumes are applied per source, not per track.
        Assert.Equal([0.4f, 0.75f, 1.0f], sink.Volumes.Select(v => v.Volume));
        // Every created source is marked active so it actually produces audio.
        Assert.Equal(sink.CreatedSources.Select(s => s.Name), sink.Activated.Select(s => s.Name));
        Assert.Empty(sink.Deactivated);
    }

    [Fact]
    public void TheService_CreatesOneEncoderPerTrackBoundToThatMixerAndAssignedToThatSlot()
    {
        var sink = new FakeSink();
        var service = new AudioRoutingService(sink);
        var plan = AudioRoutingPlanner.Plan(new List<AudioTrack>
        {
            new() { Name = "Game", Sources = { new AudioSource { Name = "Game audio", Kind = AudioSourceKind.Output, Volume = 1.0f } } },
            new() { Name = "Mic", Sources = { new AudioSource { Name = "Mic", Kind = AudioSourceKind.Input, Volume = 1.0f } } },
            new() { Name = "Discord + Mic", Sources =
            {
                new AudioSource { Name = "Discord", Kind = AudioSourceKind.Output, Volume = 0.5f },
            } },
        });

        using var routing = service.Wire(plan);

        // One encoder per track, each created against that track's mixer.
        Assert.Equal(3, sink.CreatedEncoders.Count);
        Assert.Equal([0, 1, 2], sink.CreatedEncoders.Select(e => e.MixerIndex));

        // Each encoder is assigned to the output slot matching its track.
        Assert.Equal(3, sink.Assigned.Count);
        Assert.Equal([0, 1, 2], sink.Assigned.Select(a => a.OutputSlot));
        Assert.Equal(sink.CreatedEncoders.Select(e => e.Encoder), sink.Assigned.Select(a => a.Encoder));
    }

    [Fact]
    public void TheService_BuildsTheMetadataLayoutFromThePlan()
    {
        var sink = new FakeSink();
        var service = new AudioRoutingService(sink);
        var plan = AudioRoutingPlanner.Plan(new List<AudioTrack>
        {
            new() { Name = "Game", Sources =
            {
                new AudioSource { Name = "Game audio", Kind = AudioSourceKind.Output, Volume = 0.8f },
            } },
            new() { Name = "Discord + Mic", Sources =
            {
                new AudioSource { Name = "Discord", Kind = AudioSourceKind.Output, Volume = 0.5f },
                new AudioSource { Name = "Mic", Kind = AudioSourceKind.Input, Volume = 1.0f },
            } },
        });

        using var routing = service.Wire(plan);

        Assert.Equal(2, routing.Metadata.AudioTracks.Count);

        var game = routing.Metadata.AudioTracks[0];
        Assert.Equal(0, game.Index);
        Assert.Equal("Game", game.Name);
        var gameSource = Assert.Single(game.Sources);
        Assert.Equal("Game audio", gameSource.Name);
        Assert.Equal(0.8f, gameSource.Volume);

        var merged = routing.Metadata.AudioTracks[1];
        Assert.Equal(1, merged.Index);
        Assert.Equal("Discord + Mic", merged.Name);
        Assert.Equal(2, merged.Sources.Count);
        Assert.Equal("Discord", merged.Sources[0].Name);
        Assert.Equal(0.5f, merged.Sources[0].Volume);
        Assert.Equal("Mic", merged.Sources[1].Name);
        Assert.Equal(1.0f, merged.Sources[1].Volume);
    }

    [Fact]
    public void ZeroTracks_WiresNothingAndCarriesNoMetadata()
    {
        var sink = new FakeSink();
        var service = new AudioRoutingService(sink);
        var plan = AudioRoutingPlanner.Plan([]);

        using var routing = service.Wire(plan);

        Assert.Empty(sink.CreatedSources);
        Assert.Empty(sink.CreatedEncoders);
        Assert.Empty(sink.Assigned);
        Assert.Empty(routing.Metadata.AudioTracks);
    }

    [Fact]
    public void DisposingTheRouting_DeactivatesEveryCreatedSource()
    {
        var sink = new FakeSink();
        var service = new AudioRoutingService(sink);
        var plan = AudioRoutingPlanner.Plan(new List<AudioTrack>
        {
            new() { Name = "Game", Sources = { new AudioSource { Name = "Game audio", Kind = AudioSourceKind.Output, Volume = 1.0f } } },
        });

        var routing = service.Wire(plan);
        routing.Dispose();

        // Every MarkActive at wire time is balanced by a MarkInactive at dispose.
        Assert.Equal(sink.CreatedSources.Select(s => s.Name), sink.Deactivated.Select(s => s.Name));
        Assert.Equal(sink.Activated.Count, sink.Deactivated.Count);
    }

    // ---- the fake sink ----

    private sealed class FakeSink : IAudioRoutingSink
    {
        internal List<FakeSource> CreatedSources { get; } = [];

        internal List<(FakeSource Source, int MixerIndex)> Routed { get; } = [];

        internal List<(FakeSource Source, float Volume)> Volumes { get; } = [];

        internal List<FakeSource> Activated { get; } = [];

        internal List<FakeSource> Deactivated { get; } = [];

        internal List<(FakeEncoder Encoder, int MixerIndex)> CreatedEncoders { get; } = [];

        internal List<(FakeEncoder Encoder, int OutputSlot)> Assigned { get; } = [];

        public IAudioRoutedSource CreateCaptureSource(AudioSourceKind kind, string name)
        {
            var source = new FakeSource(name);
            CreatedSources.Add(source);
            return source;
        }

        public void RouteSourceToMixer(IAudioRoutedSource source, int mixerIndex) =>
            Routed.Add(((FakeSource)source, mixerIndex));

        public void SetSourceVolume(IAudioRoutedSource source, float volume) =>
            Volumes.Add(((FakeSource)source, volume));

        public void ActivateSource(IAudioRoutedSource source) => Activated.Add((FakeSource)source);

        public void DeactivateSource(IAudioRoutedSource source) => Deactivated.Add((FakeSource)source);

        public IAudioTrackEncoder CreateTrackEncoder(int mixerIndex, string name)
        {
            var encoder = new FakeEncoder(name);
            CreatedEncoders.Add((encoder, mixerIndex));
            return encoder;
        }

        public void AssignEncoderToSlot(IAudioTrackEncoder encoder, int outputSlot) =>
            Assigned.Add(((FakeEncoder)encoder, outputSlot));
    }

    private sealed class FakeSource(string name) : IAudioRoutedSource
    {
        internal string Name { get; } = name;
    }

    private sealed class FakeEncoder(string name) : IAudioTrackEncoder
    {
        internal string Name { get; } = name;
    }
}
