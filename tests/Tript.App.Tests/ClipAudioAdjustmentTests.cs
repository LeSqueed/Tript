// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.App;
using Tript.Media;
using Xunit;

namespace Tript.App.Tests;

public class ClipAudioAdjustmentTests
{
    [Fact]
    public void NoAudioPayload_ProducesNoAdjustments()
    {
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
        var adjustments = AppController.BuildAudioAdjustments(new CreateClipParameters
        {
            AudioTrackVolumes = new() { [key] = 0.5 },
        });

        Assert.Empty(adjustments);
    }

    [Fact]
    public void AnUnusableVolumeIsIgnoredRatherThanClamped()
    {
        var adjustments = AppController.BuildAudioAdjustments(new CreateClipParameters
        {
            AudioTrackVolumes = new() { ["0"] = 4.0, ["1"] = double.NaN },
        });

        Assert.Empty(adjustments);
    }

    [Fact]
    public void OnlyNamedTracksAppear()
    {
        var adjustments = AppController.BuildAudioAdjustments(new CreateClipParameters
        {
            MutedAudioTracks = ["0"],
        });

        Assert.Single(adjustments);
        Assert.DoesNotContain(adjustments, adjustment => adjustment.SourceTrackIndex == 1);
    }
}
