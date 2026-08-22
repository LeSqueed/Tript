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

    // A source's device selection must reach the sink so the capture source is created against the
    // right device: the WASAPI-free assertion that the routing writes device_id is that the sink
    // receives it per source. A source without a selection still gets a source, for the platform
    // default device (deviceId null).
    [Fact]
    public void TheService_PassesEachSourcesDeviceIdThroughToTheSink()
    {
        var sink = new FakeSink();
        var service = new AudioRoutingService(sink);
        const string micId = "\\\\?\\SWD\\MMDEVAPI\\{0.0.1.00000000}.{11111111-2222-3333-4444-555555555555}";
        var plan = AudioRoutingPlanner.Plan(new List<AudioTrack>
        {
            new() { Name = "Game", Sources =
            {
                new AudioSource { Name = "Game audio", Kind = AudioSourceKind.Output, Volume = 1.0f },
                new AudioSource { Name = "Mic", Kind = AudioSourceKind.Input, Volume = 1.0f, DeviceId = micId },
            } },
        });

        using var routing = service.Wire(plan);

        Assert.Equal(2, sink.CreatedSources.Count);
        Assert.Null(sink.CreatedSources[0].DeviceId);
        Assert.Equal(micId, sink.CreatedSources[1].DeviceId);
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

    // Every capture source the sink created is the routing's to release. Leaving them to a
    // finalizer is what fills the OBS context with live handles at the moment it is shut down.
    [Fact]
    public void DisposingTheRouting_DisposesEveryCreatedSourceAndEncoder()
    {
        var sink = new FakeSink();
        var service = new AudioRoutingService(sink);
        var plan = AudioRoutingPlanner.Plan(new List<AudioTrack>
        {
            new() { Name = "Game", Sources =
            {
                new AudioSource { Name = "Game audio", Kind = AudioSourceKind.Output, Volume = 1.0f },
                new AudioSource { Name = "Discord", Kind = AudioSourceKind.Output, Volume = 0.5f },
            } },
            new() { Name = "Mic", Sources = { new AudioSource { Name = "Mic", Kind = AudioSourceKind.Input, Volume = 1.0f } } },
        });

        var routing = service.Wire(plan);
        Assert.All(sink.CreatedSources, source => Assert.False(source.Disposed));

        routing.Dispose();

        Assert.All(sink.CreatedSources, source => Assert.True(source.Disposed));
        Assert.All(sink.CreatedEncoders, entry => Assert.True(entry.Encoder.Disposed));
    }

    // A source must be deactivated before it is released, not after: DeactivateSource reaches into
    // the source the sink created.
    [Fact]
    public void DisposingTheRouting_DeactivatesEachSourceBeforeReleasingIt()
    {
        var sink = new FakeSink();
        var service = new AudioRoutingService(sink);
        var plan = AudioRoutingPlanner.Plan(new List<AudioTrack>
        {
            new() { Name = "Game", Sources = { new AudioSource { Name = "Game audio", Kind = AudioSourceKind.Output, Volume = 1.0f } } },
        });

        var routing = service.Wire(plan);
        routing.Dispose();

        Assert.All(sink.Deactivated, source => Assert.False(source.WasDisposedWhenDeactivated));
    }

    // Dispose runs on the recorder's stop path and on its failure path, and both can reach the same
    // routing. A second pass must not deactivate a source that is already released.
    [Fact]
    public void DisposingTheRoutingTwice_ReleasesEverythingOnce()
    {
        var sink = new FakeSink();
        var service = new AudioRoutingService(sink);
        var plan = AudioRoutingPlanner.Plan(new List<AudioTrack>
        {
            new() { Name = "Game", Sources = { new AudioSource { Name = "Game audio", Kind = AudioSourceKind.Output, Volume = 1.0f } } },
        });

        var routing = service.Wire(plan);
        routing.Dispose();
        routing.Dispose();

        Assert.Single(sink.Deactivated);
        Assert.All(sink.CreatedSources, source => Assert.Equal(1, source.DisposeCount));
    }

    [Fact]
    public void WiringFailure_RollsBackSourcesCreatedBeforeTheFailure()
    {
        var sink = new FakeSink { FailOnCreateSource = 2 };
        var plan = AudioRoutingPlanner.Plan(new List<AudioTrack>
        {
            new() { Name = "Game", Sources =
            {
                new AudioSource { Name = "Game", Kind = AudioSourceKind.Output, Volume = 1f },
                new AudioSource { Name = "Mic", Kind = AudioSourceKind.Input, Volume = 1f },
            } }
        });

        Assert.Throws<InvalidOperationException>(() => new AudioRoutingService(sink).Wire(plan));

        Assert.Single(sink.CreatedSources);
        Assert.True(sink.CreatedSources[0].Disposed);
        Assert.Single(sink.Deactivated);
    }

    [Fact]
    public void WiringFailure_RollsBackEncodersAndSourcesCreatedBeforeAssignmentFails()
    {
        var sink = new FakeSink { FailOnAssign = 2 };
        var plan = AudioRoutingPlanner.Plan(new List<AudioTrack>
        {
            new() { Name = "Game", Sources =
            {
                new AudioSource { Name = "Game", Kind = AudioSourceKind.Output, Volume = 1f }
            } },
            new() { Name = "Mic", Sources =
            {
                new AudioSource { Name = "Mic", Kind = AudioSourceKind.Input, Volume = 1f }
            } }
        });

        Assert.Throws<InvalidOperationException>(() => new AudioRoutingService(sink).Wire(plan));

        Assert.Equal(2, sink.CreatedSources.Count);
        Assert.All(sink.CreatedSources, source => Assert.True(source.Disposed));
        Assert.Equal(2, sink.Deactivated.Count);
        Assert.Equal(2, sink.CreatedEncoders.Count);
        Assert.All(sink.CreatedEncoders, entry => Assert.True(entry.Encoder.Disposed));
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

        internal int? FailOnCreateSource { get; init; }

        internal int? FailOnAssign { get; init; }

        private int _createSourceCalls;

        private int _assignCalls;

        public IAudioRoutedSource CreateCaptureSource(AudioSourceKind kind, string name, string? deviceId)
        {
            if (++_createSourceCalls == FailOnCreateSource)
                throw new InvalidOperationException("capture source creation failed");

            var source = new FakeSource(name, deviceId);
            CreatedSources.Add(source);
            return source;
        }

        public void RouteSourceToMixer(IAudioRoutedSource source, int mixerIndex) =>
            Routed.Add(((FakeSource)source, mixerIndex));

        public void SetSourceVolume(IAudioRoutedSource source, float volume) =>
            Volumes.Add(((FakeSource)source, volume));

        public void ActivateSource(IAudioRoutedSource source) => Activated.Add((FakeSource)source);

        public void DeactivateSource(IAudioRoutedSource source)
        {
            var fake = (FakeSource)source;
            fake.WasDisposedWhenDeactivated = fake.Disposed;
            Deactivated.Add(fake);
        }

        public IAudioTrackEncoder CreateTrackEncoder(int mixerIndex, string name)
        {
            var encoder = new FakeEncoder(name);
            CreatedEncoders.Add((encoder, mixerIndex));
            return encoder;
        }

        public void AssignEncoderToSlot(IAudioTrackEncoder encoder, int outputSlot)
        {
            if (++_assignCalls == FailOnAssign)
                throw new InvalidOperationException("encoder assignment failed");

            Assigned.Add(((FakeEncoder)encoder, outputSlot));
        }
    }

    private sealed class FakeSource(string name, string? deviceId) : IAudioRoutedSource, IDisposable
    {
        internal string Name { get; } = name;

        internal string? DeviceId { get; } = deviceId;

        internal int DisposeCount { get; private set; }

        internal bool Disposed => DisposeCount > 0;

        internal bool WasDisposedWhenDeactivated { get; set; }

        public void Dispose() => DisposeCount++;
    }

    private sealed class FakeEncoder(string name) : IAudioTrackEncoder, IDisposable
    {
        internal string Name { get; } = name;

        internal bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
