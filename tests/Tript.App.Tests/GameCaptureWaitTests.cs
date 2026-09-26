// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Recorder;
using Xunit;

namespace Tript.App.Tests;

public sealed class GameCaptureWaitTests
{
    private static readonly TimeSpan PolicyTimeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void WithoutAScreenFallback_TheWaitNeverGivesUp(bool? captureLayerLoaded)
    {
        Assert.Equal(Timeout.InfiniteTimeSpan, GameCaptureWait.Deadline(false, PolicyTimeout, captureLayerLoaded));
    }

    [Fact]
    public void AGameLaunchedWithTheCaptureLayer_GetsTheLongerLimit()
    {
        Assert.Equal(GameCaptureWait.LaunchedForCaptureLimit, GameCaptureWait.Deadline(true, PolicyTimeout, true));
    }

    [Fact]
    public void ALongerConfiguredTimeout_IsKeptForAGameLaunchedWithTheCaptureLayer()
    {
        var configured = TimeSpan.FromMinutes(5);

        Assert.Equal(configured, GameCaptureWait.Deadline(true, configured, true));
    }

    [Fact]
    public void AGameLaunchedWithoutTheCaptureLayer_IsNotWaitedFor()
    {
        Assert.Null(GameCaptureWait.Deadline(true, PolicyTimeout, false));
    }

    [Fact]
    public void AnUnknownLaunch_KeepsTheConfiguredTimeout()
    {
        Assert.Equal(PolicyTimeout, GameCaptureWait.Deadline(true, PolicyTimeout, null));
    }

    [Fact]
    public void TheProcessId_IsReadBackFromADetectedGameOwner()
    {
        var owner = DetectedGameTracker.OwnerOf(new DetectedGameProcess("Overwatch", 4001, "Overwatch", "/games/Overwatch.exe"));

        Assert.Equal(4001, DetectedGameTracker.ProcessIdOf(owner));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-owner")]
    public void AnOwnerWithoutAProcessId_HasNone(string? owner)
    {
        Assert.Null(DetectedGameTracker.ProcessIdOf(owner));
    }
}
