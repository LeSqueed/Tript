// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Obs.IntegrationTests;

public sealed class ObsSourceAudioTests
{
    private const string PulseInputCaptureId = "pulse_input_capture";

    [SkippableFact]
    public void AudioMixers_RoundTripTheBitmaskOnAnAudioSource()
    {
        using var session = ObsSession.StartWithAudioSources();
        using var source = ObsSource.Create(PulseInputCaptureId, "routed");

        Assert.Equal(0xFFu, source.AudioMixers);

        source.AudioMixers = 0b001u;
        Assert.Equal(0b001u, source.AudioMixers);

        source.AudioMixers = 0b101u;
        Assert.Equal(0b101u, source.AudioMixers);
    }

    [SkippableFact]
    public void Volume_RoundTripsThePerSourceGain()
    {
        using var session = ObsSession.StartWithAudioSources();
        using var source = ObsSource.Create(PulseInputCaptureId, "gain");

        Assert.Equal(1.0f, source.Volume);

        source.Volume = 0.4f;
        Assert.Equal(0.4f, source.Volume);

        source.Volume = 1.0f;
        Assert.Equal(1.0f, source.Volume);
    }

    [SkippableFact]
    public void ActiveIncrement_MarksTheSourceActiveUntilBalanced()
    {
        using var session = ObsSession.StartWithAudioSources();
        using var source = ObsSource.Create(PulseInputCaptureId, "live");

        Assert.False(source.IsActive);

        source.MarkActive();
        Assert.True(source.IsActive);

        source.MarkInactive();
        Assert.False(source.IsActive);
    }

    [SkippableFact]
    public void ActiveCounter_NeedsABalancingDeactivationForEachMark()
    {
        using var session = ObsSession.StartWithAudioSources();
        using var source = ObsSource.Create(PulseInputCaptureId, "shared");

        source.MarkActive();
        source.MarkActive();
        Assert.True(source.IsActive);

        source.MarkInactive();
        Assert.True(source.IsActive);

        source.MarkInactive();
        Assert.False(source.IsActive);
    }
}
