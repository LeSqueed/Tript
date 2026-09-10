// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System;
using System.Collections.Generic;
using Tript.Core;
using Xunit;

namespace Tript.Settings.Tests;

public class SettingsResolverTests
{
    private static Settings WithGame(string id, Action<GameSetting> configure)
    {
        var settings = new Settings();
        var game = new GameSetting { Id = id, Name = "Test Game" };
        configure(game);
        settings.Game.GameList.Add(game);
        return settings;
    }

    [Fact]
    public void Resolve_NoGameOverride_UsesGlobalValues()
    {
        var settings = new Settings();
        settings.Recording.Mode = RecordingMode.Session;
        settings.Recording.Fps = 30;
        settings.Recording.Encoder = "x264";

        var resolved = SettingsResolver.Resolve(settings, gameId: "unknown-game");

        Assert.Equal(RecordingMode.Session, resolved.Mode);
        Assert.Equal(30, resolved.Fps);
        Assert.Equal("x264", resolved.Encoder);
    }

    [Fact]
    public void Resolve_WithGameOverride_UsesOverrideWhereSetAndGlobalElsewhere()
    {
        var settings = WithGame("ow", game =>
        {
            game.RecordingModeOverride = new GameRecordingModeOverride { Mode = RecordingMode.Session };
            game.QualityOverride = new GameQualityOverride
            {
                Fps = 240,
                Encoder = "nvenc",
            };
        });
        settings.Recording.Mode = RecordingMode.SessionWithReplayBuffer;
        settings.Recording.Fps = 60;
        settings.Recording.Encoder = "x264";
        settings.Recording.ResolutionWidth = 1920;

        var resolved = SettingsResolver.Resolve(settings, gameId: "ow");

        Assert.Equal(RecordingMode.Session, resolved.Mode);

        Assert.Equal(240, resolved.Fps);
        Assert.Equal("nvenc", resolved.Encoder);
        Assert.Equal(1920, resolved.ResolutionWidth);
    }

    [Fact]
    public void Resolve_GameWithNoModeOverride_InheritsGlobalMode()
    {
        var settings = WithGame("ow", game =>
        {
            game.QualityOverride = new GameQualityOverride { Quality = 3 };
        });
        settings.Recording.Mode = RecordingMode.SessionWithReplayBuffer;

        var resolved = SettingsResolver.Resolve(settings, gameId: "ow");

        Assert.Equal(RecordingMode.SessionWithReplayBuffer, resolved.Mode);
        Assert.Equal(3, resolved.Quality);
    }

    [Fact]
    public void Resolve_ProducesTheFlatShapeTheRecorderConsumes()
    {
        var settings = new Settings();
        settings.Recording.Mode = RecordingMode.SessionWithReplayBuffer;
        settings.Buffer.Duration = TimeSpan.FromSeconds(45);
        settings.Audio.Tracks.Add(new AudioTrack { Name = "Game", Sources = { new AudioSource { Name = "Game audio", Kind = AudioSourceKind.Output } } });

        var resolved = SettingsResolver.Resolve(settings);

        Assert.IsType<ResolvedRecorderSettings>(resolved);
        Assert.Equal(RecordingMode.SessionWithReplayBuffer, resolved.Mode);
        Assert.True(resolved.BufferEnabled);
        Assert.Equal(TimeSpan.FromSeconds(45), resolved.BufferDuration);
        var track = Assert.Single(resolved.AudioTracks);
        Assert.Equal("Game", track.Name);
        var source = Assert.Single(track.Sources);
        Assert.Equal("Game audio", source.Name);
    }

    [Theory]
    [InlineData(RecordingMode.SessionWithReplayBuffer, true)]
    [InlineData(RecordingMode.ReplayBufferOnly, true)]
    [InlineData(RecordingMode.Session, false)]
    public void Resolve_BufferEnabledTracksReplayModes(RecordingMode mode, bool expected)
    {
        var settings = new Settings();
        settings.Recording.Mode = mode;

        Assert.Equal(expected, SettingsResolver.Resolve(settings).BufferEnabled);
    }

    [Fact]
    public void Resolve_CarriesTheRateControlChoiceForEveryGame()
    {
        var settings = WithGame("ow", game =>
        {
            game.QualityOverride = new GameQualityOverride { Quality = 18, Encoder = "obs_x264" };
        });
        settings.Recording.RateControl = RateControlMode.Cbr;
        settings.Recording.BitrateKbps = 22_000;
        settings.Recording.MaxBitrateKbps = 33_000;

        var resolved = SettingsResolver.Resolve(settings, gameId: "ow");

        Assert.Equal(RateControlMode.Cbr, resolved.RateControl);
        Assert.Equal(22_000, resolved.BitrateKbps);
        Assert.Equal(33_000, resolved.MaxBitrateKbps);

        var clone = resolved.Clone();
        Assert.Equal(RateControlMode.Cbr, clone.RateControl);
        Assert.Equal(22_000, clone.BitrateKbps);
        Assert.Equal(33_000, clone.MaxBitrateKbps);
    }

    [Fact]
    public void Resolve_DefaultsToConstantQuality()
    {
        var resolved = SettingsResolver.Resolve(new Settings());

        Assert.Equal(RateControlMode.Cqp, resolved.RateControl);
        Assert.Equal(15_000, resolved.BitrateKbps);
    }

    [Fact]
    public void Resolve_CarriesTheCapturePolicy()
    {
        var settings = new Settings();
        settings.Capture.Method = DisplayCaptureMethod.Game;
        settings.Capture.Display = "monitor-2";
        settings.Game.GameCaptureTimeout = TimeSpan.FromSeconds(15);

        var resolved = SettingsResolver.Resolve(settings);

        Assert.Equal(DisplayCaptureMethod.Game, resolved.CaptureMethod);
        Assert.Equal("monitor-2", resolved.Display);
        Assert.Equal(TimeSpan.FromSeconds(15), resolved.GameCaptureTimeout);

        var clone = resolved.Clone();
        Assert.Equal(DisplayCaptureMethod.Game, clone.CaptureMethod);
        Assert.Equal("monitor-2", clone.Display);
        Assert.Equal(TimeSpan.FromSeconds(15), clone.GameCaptureTimeout);
    }

    [Fact]
    public void Resolve_DefaultsToAutoCaptureOnThePrimaryMonitor()
    {
        var resolved = SettingsResolver.Resolve(new Settings());

        Assert.Equal(DisplayCaptureMethod.Auto, resolved.CaptureMethod);
        Assert.Null(resolved.Display);
        Assert.Equal(TimeSpan.FromSeconds(10), resolved.GameCaptureTimeout);
    }

    [Fact]
    public void CaptureMethod_PerGameOverride_WinsOverTheGlobal()
    {
        var settings = new Settings();
        settings.Capture.Method = DisplayCaptureMethod.Game;
        settings.Game.GameList =
        [
            new GameSetting
            {
                Id = "Overwatch",
                Name = "Overwatch",
                CaptureMethodOverride = new GameCaptureMethodOverride { Method = DisplayCaptureMethod.Display },
            },
        ];

        Assert.Equal(DisplayCaptureMethod.Display, SettingsResolver.Resolve(settings, "Overwatch").CaptureMethod);
    }

    [Fact]
    public void CaptureMethod_WithoutAnOverride_InheritsTheGlobal()
    {
        var settings = new Settings();
        settings.Capture.Method = DisplayCaptureMethod.Game;
        settings.Game.GameList = [new GameSetting { Id = "Overwatch", Name = "Overwatch" }];

        Assert.Equal(DisplayCaptureMethod.Game, SettingsResolver.Resolve(settings, "Overwatch").CaptureMethod);

        Assert.Equal(DisplayCaptureMethod.Game, SettingsResolver.Resolve(settings, "Unknown").CaptureMethod);
    }

    [Fact]
    public void ClipWindow_GlobalValues_AreTheDefaults()
    {
        var (before, after) = SettingsResolver.ResolveAutomaticClipWindow(new Settings(), gameId: null);

        Assert.Equal(TimeSpan.FromSeconds(5), before);
        Assert.Equal(TimeSpan.FromSeconds(8), after);
    }

    [Fact]
    public void ClipWindow_UnknownOrNullGame_InheritsTheGlobalValues()
    {
        var settings = new Settings();

        var (unknownBefore, unknownAfter) = SettingsResolver.ResolveAutomaticClipWindow(settings, gameId: "unknown-game");
        var (nullBefore, nullAfter) = SettingsResolver.ResolveAutomaticClipWindow(settings, gameId: null);

        Assert.Equal(TimeSpan.FromSeconds(5), unknownBefore);
        Assert.Equal(TimeSpan.FromSeconds(8), unknownAfter);
        Assert.Equal(TimeSpan.FromSeconds(5), nullBefore);
        Assert.Equal(TimeSpan.FromSeconds(8), nullAfter);
    }

    [Fact]
    public void ClipWindow_BothSidesOverridden_UsesTheOverrideForEachSide()
    {
        var settings = WithGame("ow", game =>
        {
            game.AutomaticClipOverride = new GameAutomaticClipOverride { BeforeSeconds = 3, AfterSeconds = 12 };
        });

        var (before, after) = SettingsResolver.ResolveAutomaticClipWindow(settings, gameId: "ow");

        Assert.Equal(TimeSpan.FromSeconds(3), before);
        Assert.Equal(TimeSpan.FromSeconds(12), after);
    }

    [Fact]
    public void ClipWindow_BeforeOnlyOverride_UsesOverrideAndInheritsGlobalAfter()
    {
        var settings = WithGame("ow", game =>
        {
            game.AutomaticClipOverride = new GameAutomaticClipOverride { BeforeSeconds = 3 };
        });

        var (before, after) = SettingsResolver.ResolveAutomaticClipWindow(settings, gameId: "ow");

        Assert.Equal(TimeSpan.FromSeconds(3), before);
        Assert.Equal(TimeSpan.FromSeconds(8), after);
    }

    [Fact]
    public void ClipWindow_AfterOnlyOverride_UsesOverrideAndInheritsGlobalBefore()
    {
        var settings = WithGame("ow", game =>
        {
            game.AutomaticClipOverride = new GameAutomaticClipOverride { AfterSeconds = 12 };
        });

        var (before, after) = SettingsResolver.ResolveAutomaticClipWindow(settings, gameId: "ow");

        Assert.Equal(TimeSpan.FromSeconds(5), before);
        Assert.Equal(TimeSpan.FromSeconds(12), after);
    }

    [Fact]
    public void ClipWindow_GlobalAfterBeforeBefore_ClampsAfterUpToBefore()
    {
        var settings = new Settings();
        settings.Recording.AutomaticClipBeforeSeconds = 10;
        settings.Recording.AutomaticClipAfterSeconds = 4;

        var (before, after) = SettingsResolver.ResolveAutomaticClipWindow(settings, gameId: null);

        Assert.Equal(TimeSpan.FromSeconds(10), before);
        Assert.Equal(TimeSpan.FromSeconds(10), after);
    }

    [Fact]
    public void ClipWindow_OverriddenAfterBelowBefore_ClampsAfterUpToBefore()
    {
        var settings = WithGame("ow", game =>
        {
            game.AutomaticClipOverride = new GameAutomaticClipOverride { BeforeSeconds = 6, AfterSeconds = 2 };
        });

        var (before, after) = SettingsResolver.ResolveAutomaticClipWindow(settings, gameId: "ow");

        Assert.Equal(TimeSpan.FromSeconds(6), before);
        Assert.Equal(TimeSpan.FromSeconds(6), after);
    }

    [Fact]
    public void ClipWindow_NegativeBefore_ClampsBeforeToZero()
    {
        var settings = new Settings();
        settings.Recording.AutomaticClipBeforeSeconds = -3;
        settings.Recording.AutomaticClipAfterSeconds = 4;

        var (before, after) = SettingsResolver.ResolveAutomaticClipWindow(settings, gameId: null);

        Assert.Equal(TimeSpan.Zero, before);
        Assert.Equal(TimeSpan.FromSeconds(4), after);
    }

    [Fact]
    public void ClipWindow_NegativeAfter_ClampsAfterToZero()
    {
        var settings = new Settings();
        settings.Recording.AutomaticClipBeforeSeconds = 0;
        settings.Recording.AutomaticClipAfterSeconds = -3;

        var (before, after) = SettingsResolver.ResolveAutomaticClipWindow(settings, gameId: null);

        Assert.Equal(TimeSpan.Zero, before);
        Assert.Equal(TimeSpan.Zero, after);
    }

    [Fact]
    public void ClipWindow_NegativeAfterBelowPositiveBefore_ClampsThenRaisesAfterToBefore()
    {
        var settings = WithGame("ow", game =>
        {
            game.AutomaticClipOverride = new GameAutomaticClipOverride
            {
                BeforeSeconds = 6,
                AfterSeconds = -2,
            };
        });

        var (before, after) = SettingsResolver.ResolveAutomaticClipWindow(settings, gameId: "ow");

        Assert.Equal(TimeSpan.FromSeconds(6), before);
        Assert.Equal(TimeSpan.FromSeconds(6), after);
    }
}
