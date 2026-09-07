// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;
using Tript.Obs;
using Xunit;

namespace Tript.Recorder.Tests;

public sealed class ObsAudioLevelMonitorTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-10, 0.833f)]
    [InlineData(-20, 0.667f)]
    [InlineData(-30, 0.5f)]
    [InlineData(-60, 0)]
    [InlineData(float.NegativeInfinity, 0)]
    public void ObsVolumeMeterMapsDecibelsToLogMeterPosition(float decibels, float expected)
    {
        Assert.Equal(expected, ObsVolumeMeter.ToMeterPosition(decibels), 3);
    }

    [Fact]
    public void ReadCreatesOneProbePerCapturePathAndReturnsItsPeak()
    {
        var created = new List<AudioLevelSource>();
        using var monitor = new ObsAudioLevelMonitor(source =>
        {
            created.Add(source);
            return new FakeProbe(source.Kind == AudioSourceKind.Output ? 0.25f : 0.75f);
        });

        var levels = monitor.Read(
        [
            new AudioLevelSource(AudioSourceKind.Output, "speakers"),
            new AudioLevelSource(AudioSourceKind.Input, "mic"),
        ]);

        Assert.Contains(new AudioLevelSource(AudioSourceKind.Output, "speakers"), created);
        Assert.Contains(new AudioLevelSource(AudioSourceKind.Input, "mic"), created);
        Assert.Equal(0.25f, levels["speakers"]);
        Assert.Equal(0.75f, levels["mic"]);
    }

    [Fact]
    public void ReadReusesExistingProbeAndDisposesRemovedProbe()
    {
        var probe = new FakeProbe(0.5f);
        var creates = 0;
        using var monitor = new ObsAudioLevelMonitor(_ =>
        {
            creates++;
            return probe;
        });

        monitor.Read([new AudioLevelSource(AudioSourceKind.Output, "speakers")]);
        monitor.Read([new AudioLevelSource(AudioSourceKind.Output, "speakers")]);
        monitor.Read([]);

        Assert.Equal(1, creates);
        Assert.True(probe.Disposed);
    }

    private sealed class FakeProbe(float peak) : IAudioLevelProbe
    {
        internal bool Disposed { get; private set; }

        public float Peak { get; } = peak;

        public void Dispose() => Disposed = true;
    }
}
