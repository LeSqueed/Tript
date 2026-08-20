// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.App;
using Tript.Media;
using Xunit;

namespace Tript.App.Tests;

// The clip dialog's per-track volume and mute reach the engine. Before this mapping existed the
// controller hardcoded an empty list, so those controls did nothing at all — and the engine read
// the empty list as "volume 0" for every track, which is why every clip came out silent.
public class ClipAudioAdjustmentTests
{
    [Fact]
    public void NoAudioPayload_ProducesNoAdjustments()
    {
        // The common case, and the one that must stay empty: an empty list means "touch nothing",
        // which is what the engine's passthrough default relies on.
        var adjustments = AppController.BuildAudioAdjustments(new CreateClipParameters());
        Assert.Empty(adjustments);
    }

    [Fact]
    public void VolumeLandsOnTheTrackItNames()
    {
        var adjustments = AppController.BuildAudioAdjustments(new CreateClipParameters
        {
            AudioTrackVolumes = new() { ["1"] = 0.25 },
        });

        var adjustment = Assert.Single(adjustments);
        Assert.Equal(1, adjustment.SourceTrackIndex);
        Assert.Equal(0.25, adjustment.Volume);
        Assert.False(adjustment.Muted);
    }

    [Fact]
    public void MutedTrackIsMutedAndKeepsFullVolumeOtherwise()
    {
        var adjustments = AppController.BuildAudioAdjustments(new CreateClipParameters
        {
            MutedAudioTracks = ["0"],
        });

        var adjustment = Assert.Single(adjustments);
        Assert.Equal(0, adjustment.SourceTrackIndex);
        Assert.True(adjustment.Muted);
        Assert.Equal(1.0, adjustment.Volume);
    }

    [Fact]
    public void VolumeAndMuteOnTheSameTrackCombine()
    {
        var adjustments = AppController.BuildAudioAdjustments(new CreateClipParameters
        {
            AudioTrackVolumes = new() { ["2"] = 0.5 },
            MutedAudioTracks = ["2"],
        });

        var adjustment = Assert.Single(adjustments);
        Assert.Equal(2, adjustment.SourceTrackIndex);
        Assert.True(adjustment.Muted);
        Assert.Equal(0.5, adjustment.Volume);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    public void AKeyThatIsNotATrackPositionIsDropped(string key)
    {
        // Dropped, never defaulted. A default AudioTrackAdjustment carries Volume 0, so turning an
        // unknown key into one would silence a track the user never touched.
        var adjustments = AppController.BuildAudioAdjustments(new CreateClipParameters
        {
            AudioTrackVolumes = new() { [key] = 0.5 },
        });

        Assert.Empty(adjustments);
    }

    [Fact]
    public void AnUnusableVolumeIsIgnoredRatherThanClamped()
    {
        // A value outside 0..1 says nothing about what the user wanted, so the track is left alone.
        var adjustments = AppController.BuildAudioAdjustments(new CreateClipParameters
        {
            AudioTrackVolumes = new() { ["0"] = 4.0, ["1"] = double.NaN },
        });

        Assert.Empty(adjustments);
    }

    [Fact]
    public void OnlyNamedTracksAppear()
    {
        // Track 1 is untouched, so it must not appear at all — the engine passes it through, and an
        // adjustment saying "volume 1.0" would be indistinguishable from one the user chose.
        var adjustments = AppController.BuildAudioAdjustments(new CreateClipParameters
        {
            MutedAudioTracks = ["0"],
        });

        Assert.Single(adjustments);
        Assert.DoesNotContain(adjustments, adjustment => adjustment.SourceTrackIndex == 1);
    }
}
