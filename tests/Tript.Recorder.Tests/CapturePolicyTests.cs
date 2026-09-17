// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;
using Xunit;

namespace Tript.Recorder.Tests;

public sealed class CapturePolicyTests
{
    [Fact]
    public void Auto_HasBothLayers()
    {
        var policy = new CapturePolicy(DisplayCaptureMethod.Auto, null, TimeSpan.FromSeconds(10));

        Assert.True(policy.IncludesDisplayCapture);
        Assert.True(policy.IncludesGameCapture);
    }

    [Fact]
    public void Game_HasNoDisplayLayer()
    {
        var policy = new CapturePolicy(DisplayCaptureMethod.Game, null, TimeSpan.FromSeconds(10));

        Assert.False(policy.IncludesDisplayCapture);
        Assert.True(policy.IncludesGameCapture);
    }

    [Fact]
    public void Display_HasNoGameCaptureSourceAtAll()
    {
        var policy = new CapturePolicy(DisplayCaptureMethod.Display, null, TimeSpan.FromSeconds(10));

        Assert.True(policy.IncludesDisplayCapture);
        Assert.False(policy.IncludesGameCapture);
    }

    [Fact]
    public void TheDefaultPolicy_IsTheSettingsModelsDefault()
    {
        Assert.Equal(DisplayCaptureMethod.Auto, new CaptureSettings().Method);
        Assert.Equal(DisplayCaptureMethod.Auto, CapturePolicy.Default.Method);
        Assert.Null(CapturePolicy.Default.PreferredDisplayId);
        Assert.Equal(new GameSettings().GameCaptureTimeout, CapturePolicy.Default.GameCaptureTimeout);
    }

    [Fact]
    public void ThePolicy_ComesFromTheResolvedSettings()
    {
        var settings = new Settings.Settings();
        settings.Capture.Method = DisplayCaptureMethod.Display;
        settings.Capture.Display = "\\\\?\\DISPLAY#DEL41A8#5&2b1f9e4&0&UID4353";
        settings.Game.GameCaptureTimeout = TimeSpan.FromSeconds(25);

        var policy = CapturePolicy.From(SettingsResolver.Resolve(settings));

        Assert.Equal(DisplayCaptureMethod.Display, policy.Method);
        Assert.Equal(settings.Capture.Display, policy.PreferredDisplayId);
        Assert.Equal(TimeSpan.FromSeconds(25), policy.GameCaptureTimeout);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ANonsenseTimeout_FallsBackToTheModelsDefault(int seconds)
    {
        var settings = new Settings.Settings();
        settings.Game.GameCaptureTimeout = TimeSpan.FromSeconds(seconds);

        Assert.Equal(CapturePolicy.Default.GameCaptureTimeout,
            CapturePolicy.From(SettingsResolver.Resolve(settings)).GameCaptureTimeout);
    }

    [Fact]
    public void OnlyTheGameMethod_UsesTheConfiguredTimeoutAsItsDeadline()
    {
        var timeout = TimeSpan.FromSeconds(3);

        Assert.Equal(timeout, GameCaptureHookProbe.DeadlineFor(
            new CapturePolicy(DisplayCaptureMethod.Game, null, timeout)));

        Assert.NotEqual(timeout, GameCaptureHookProbe.DeadlineFor(
            new CapturePolicy(DisplayCaptureMethod.Auto, null, timeout)));
    }

    [Fact]
    public void CapturePolicy_RejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => CapturePolicy.From(null!));
        Assert.Throws<ArgumentNullException>(() => GameCaptureHookProbe.DeadlineFor(null!));
    }
}
