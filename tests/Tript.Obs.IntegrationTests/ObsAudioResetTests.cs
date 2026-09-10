// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Obs.IntegrationTests;

public sealed class ObsAudioResetTests
{
    [SkippableTheory]
    [InlineData(48_000u, ObsSpeakerLayout.Stereo)]
    [InlineData(44_100u, ObsSpeakerLayout.Stereo)]
    [InlineData(48_000u, ObsSpeakerLayout.Mono)]
    [InlineData(48_000u, ObsSpeakerLayout.FivePointOne)]
    [InlineData(48_000u, ObsSpeakerLayout.SevenPointOne)]
    public void AudioReset_ReadsBackTheRateAndLayoutItWasGiven(uint samplesPerSecond, ObsSpeakerLayout speakers)
    {
        using var session = ObsSession.Start();

        Assert.True(session.Runtime.ResetAudio(new ObsAudioSettings
        {
            SamplesPerSecond = samplesPerSecond,
            Speakers = speakers
        }));

        Assert.True(session.Runtime.TryGetAudioInfo(out var readBack));
        Assert.NotNull(readBack);
        Assert.Equal(samplesPerSecond, readBack.SamplesPerSecond);
        Assert.Equal(speakers, readBack.Speakers);
    }

    [SkippableFact]
    public void SevenPointOne_IsSpeakerLayoutEightNotSeven()
    {
        Assert.Equal(8, (int)ObsSpeakerLayout.SevenPointOne);

        using var session = ObsSession.Start();
        Assert.True(session.Runtime.ResetAudio(new ObsAudioSettings { Speakers = ObsSpeakerLayout.SevenPointOne }));
        Assert.True(session.Runtime.TryGetAudioInfo(out var readBack));
        Assert.Equal(8, (int)readBack!.Speakers);
    }

    [SkippableFact]
    public void BeforeTheFirstReset_NoAudioMixExists()
    {
        using var session = ObsSession.Start();

        Assert.False(session.Runtime.HasAudio);
        Assert.False(session.Runtime.TryGetAudioInfo(out _));
    }

    [SkippableFact]
    public void ASecondAudioReset_ReplacesTheFirst()
    {
        using var session = ObsSession.Start();

        Assert.True(session.Runtime.ResetAudio(new ObsAudioSettings { SamplesPerSecond = 48_000 }));
        Assert.True(session.Runtime.ResetAudio(new ObsAudioSettings { SamplesPerSecond = 44_100 }));

        Assert.True(session.Runtime.TryGetAudioInfo(out var readBack));
        Assert.Equal(44_100u, readBack!.SamplesPerSecond);
    }
}
