// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;
using Xunit;

namespace Tript.Recorder.Tests;

// The sink's default source-type resolver. The ids are registered by the platform audio module
// (linux-pulseaudio, win-wasapi), so a wrong id is not a compile or startup problem: it surfaces as
// obs_source_create returning null when the routing is wired at record time, on that platform only.
public sealed class AudioSourceTypeIdTests
{
    [Fact]
    public void ThePulseaudioMap_UsesTheIdsLinuxPulseaudioRegisters()
    {
        Assert.Equal("pulse_input_capture", ObsAudioRoutingSink.PulseAudioSourceTypeId(AudioSourceKind.Input));
        Assert.Equal("pulse_output_capture", ObsAudioRoutingSink.PulseAudioSourceTypeId(AudioSourceKind.Output));
    }

    [Fact]
    public void TheWasapiMap_UsesTheIdsWinWasapiRegisters()
    {
        Assert.Equal("wasapi_input_capture", ObsAudioRoutingSink.WasapiSourceTypeId(AudioSourceKind.Input));
        Assert.Equal("wasapi_output_capture", ObsAudioRoutingSink.WasapiSourceTypeId(AudioSourceKind.Output));
    }

    [Theory]
    [InlineData(AudioSourceKind.Input)]
    [InlineData(AudioSourceKind.Output)]
    public void TheDefaultResolver_PicksThisPlatformsMap(AudioSourceKind kind)
    {
        var expected = OperatingSystem.IsWindows()
            ? ObsAudioRoutingSink.WasapiSourceTypeId(kind)
            : ObsAudioRoutingSink.PulseAudioSourceTypeId(kind);

        Assert.Equal(expected, ObsAudioRoutingSink.DefaultSourceTypeId(kind));
    }

    // The two maps must not be the same strings: that is the bug this pins — the resolver used to
    // hand the pulseaudio ids to every platform, so Windows created no audio source at all.
    [Theory]
    [InlineData(AudioSourceKind.Input)]
    [InlineData(AudioSourceKind.Output)]
    public void TheTwoPlatformMaps_DoNotShareIds(AudioSourceKind kind) =>
        Assert.NotEqual(
            ObsAudioRoutingSink.PulseAudioSourceTypeId(kind),
            ObsAudioRoutingSink.WasapiSourceTypeId(kind));

    [Fact]
    public void AnUnknownKind_IsRejectedByEveryMap()
    {
        const AudioSourceKind unknown = (AudioSourceKind)(-1);

        Assert.Throws<ArgumentOutOfRangeException>(() => ObsAudioRoutingSink.PulseAudioSourceTypeId(unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => ObsAudioRoutingSink.WasapiSourceTypeId(unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => ObsAudioRoutingSink.DefaultSourceTypeId(unknown));
    }
}
