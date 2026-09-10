// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;
using Xunit;

namespace Tript.Recorder.Tests;

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

        Assert.Equal([0, 0, 1], sink.Routed.Select(r => r.MixerIndex));

        Assert.Equal([0.4f, 0.75f, 1.0f], sink.Volumes.Select(v => v.Volume));

        Assert.Equal(sink.CreatedSources.Select(s => s.Name), sink.Activated.Select(s => s.Name));
        Assert.Empty(sink.Deactivated);
    }

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
    public void TheService_CreatesIndependentCapturesForOneDeviceOnMultipleTracks()
    {
        const string micId = "\\\\?\\SWD\\MMDEVAPI\\{0.0.1.00000000}.{11111111-2222-3333-4444-555555555555}";
        var sink = new FakeSink();
        var plan = AudioRoutingPlanner.Plan(new List<AudioTrack>
        {
            new() { Name = "Mic", Sources = { new AudioSource { Name = "Mic", Kind = AudioSourceKind.Input, DeviceId = micId, Volume = 1.0f } } },
            new() { Name = "Mixed", Sources = { new AudioSource { Name = "Mic", Kind = AudioSourceKind.Input, DeviceId = micId, Volume = 0.5f } } },
        });

        using var routing = new AudioRoutingService(sink).Wire(plan);

        Assert.Equal(2, sink.CreatedSources.Count);
        Assert.All(sink.CreatedSources, source => Assert.Equal(micId, source.DeviceId));
        Assert.Equal([0, 1], sink.Routed.Select(source => source.MixerIndex));
        Assert.Equal([1.0f, 0.5f], sink.Volumes.Select(source => source.Volume));
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

        Assert.Equal(3, sink.CreatedEncoders.Count);
        Assert.Equal([0, 1, 2], sink.CreatedEncoders.Select(e => e.MixerIndex));

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
    public void ReplayRouting_CreatesTrackEncodersWithoutDuplicateCaptureSources()
    {
        var sink = new FakeSink();
        var plan = AudioRoutingPlanner.Plan(new List<AudioTrack>
        {
            new() { Name = "Game", Sources =
            {
                new AudioSource { Name = "Game audio", Kind = AudioSourceKind.Output, Volume = 1.0f },
            } },
            new() { Name = "Mic", Sources =
            {
                new AudioSource { Name = "Mic", Kind = AudioSourceKind.Input, Volume = 1.0f },
            } },
        });

        using var routing = new AudioRoutingService(sink).Wire(plan, includeCaptureSources: false);

        Assert.Empty(sink.CreatedSources);
        Assert.Empty(sink.Routed);
        Assert.Empty(sink.Activated);
        Assert.Equal(2, sink.CreatedEncoders.Count);
        Assert.Equal([0, 1], sink.CreatedEncoders.Select(encoder => encoder.MixerIndex));
        Assert.Equal([0, 1], sink.Assigned.Select(assignment => assignment.OutputSlot));
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

        Assert.Equal(sink.CreatedSources.Select(s => s.Name), sink.Deactivated.Select(s => s.Name));
        Assert.Equal(sink.Activated.Count, sink.Deactivated.Count);
    }

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
