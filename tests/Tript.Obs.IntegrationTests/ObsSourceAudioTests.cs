// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Obs.IntegrationTests;

// The per-source audio controls behind the multi-track routing: the mixer bitmask, the per-source
// volume, and the active pair. These are what the routing service drives through its sink — a
// source's mixers say which tracks it feeds, its volume is a per-source gain, and a source only
// produces audio while marked active.
public sealed class ObsSourceAudioTests
{
    private const string PulseInputCaptureId = "pulse_input_capture";

    [SkippableFact]
    public void AudioMixers_RoundTripTheBitmaskOnAnAudioSource()
    {
        using var session = ObsSession.StartWithAudioSources();
        using var source = ObsSource.Create(PulseInputCaptureId, "routed");

        // A fresh audio source defaults to every bit set (0xFF) — libobs has no concept of a track
        // with no mixers — and only the low MAX_AUDIO_MIXES (6) bits are real mixers.
        Assert.Equal(0xFFu, source.AudioMixers);

        // Set a single track's bit and read it back.
        source.AudioMixers = 0b001u;
        Assert.Equal(0b001u, source.AudioMixers);

        // Two sources merged onto one track share the same bit; a source can feed two tracks with
        // two bits.
        source.AudioMixers = 0b101u;
        Assert.Equal(0b101u, source.AudioMixers);
    }

    [SkippableFact]
    public void Volume_RoundTripsThePerSourceGain()
    {
        using var session = ObsSession.StartWithAudioSources();
        using var source = ObsSource.Create(PulseInputCaptureId, "gain");

        // Default gain is unity.
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

        // A freshly created source is not active; a recording marks the sources it routes so they
        // actually produce audio.
        Assert.False(source.IsActive);

        source.MarkActive();
        Assert.True(source.IsActive);

        // The counter is balanced: the matching MarkInactive brings it back.
        source.MarkInactive();
        Assert.False(source.IsActive);
    }

    // The active counter is balanced — two marks need two deactivations, and a single deactivation
    // after two marks leaves the source active. This is the shape a recorder hits when the same
    // source is routed into two tracks: each routing marks it once.
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
