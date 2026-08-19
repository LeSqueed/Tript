// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;
using Xunit;

namespace Tript.Recorder.Tests;

// Which layers a recording scene has, per capture method. This is the table the whole capture
// policy is: a display layer that is always there records the desktop when the user asked for the
// game only, and one that keeps retrying when the game does not hook immediately.
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

    // Auto is the default in the settings model, and a session built without a policy must agree
    // with it — otherwise the default recording behaviour depends on which constructor was used.
    [Fact]
    public void TheDefaultPolicy_IsTheSettingsModelsDefault()
    {
        Assert.Equal(DisplayCaptureMethod.Auto, new CaptureSettings().Method);
        Assert.Equal(DisplayCaptureMethod.Auto, CapturePolicy.Default.Method);
        Assert.Null(CapturePolicy.Default.PreferredDisplayId);
        Assert.Equal(new GameSettings().GameCaptureTimeout, CapturePolicy.Default.GameCaptureTimeout);
    }

    // The timeout the Game method waits is the one the settings model already carries; nothing else
    // in the resolver participates.
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

    // A zero or negative timeout would show the late-hook warning on the first probe.
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

    // Only the method with nothing under the game capture waits on the user's timeout; the others
    // are logging a line, not ending a recording, so they keep the longer fixed one.
    [Fact]
    public void OnlyTheGameMethod_UsesTheConfiguredTimeoutAsItsDeadline()
    {
        var timeout = TimeSpan.FromSeconds(3);

        Assert.Equal(timeout, ObsRecorderSession.HookDeadlineFor(
            new CapturePolicy(DisplayCaptureMethod.Game, null, timeout)));

        Assert.NotEqual(timeout, ObsRecorderSession.HookDeadlineFor(
            new CapturePolicy(DisplayCaptureMethod.Auto, null, timeout)));
    }

    [Fact]
    public void CapturePolicy_RejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => CapturePolicy.From(null!));
        Assert.Throws<ArgumentNullException>(() => ObsRecorderSession.HookDeadlineFor(null!));
    }
}
