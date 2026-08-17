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
        settings.Recording.Mode = RecordingMode.Hybrid;
        settings.Recording.Fps = 60;
        settings.Recording.Encoder = "x264";
        settings.Recording.ResolutionWidth = 1920;

        var resolved = SettingsResolver.Resolve(settings, gameId: "ow");

        // The per-game recording-mode override wins over the global hybrid default.
        Assert.Equal(RecordingMode.Session, resolved.Mode);
        // Quality fields with an override use the override; fields without one inherit the global.
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
        settings.Recording.Mode = RecordingMode.Hybrid;

        var resolved = SettingsResolver.Resolve(settings, gameId: "ow");

        Assert.Equal(RecordingMode.Hybrid, resolved.Mode);
        Assert.Equal(3, resolved.Quality);
    }

    // The recorder consumes a flat, resolved config — the seam the dependency map flagged. This
    // test pins the contract that the recorder does NOT depend on the settings schema: it is fed
    // a ResolvedRecorderSettings and never reaches into a Settings. (The separation is enforced
    // structurally — Tript.RecorderHarness references Tript.Settings only for the session and
    // registry, not for the merge — and this test pins the shape the recorder receives.)
    [Fact]
    public void Resolve_ProducesTheFlatShapeTheRecorderConsumes()
    {
        var settings = new Settings();
        settings.Recording.Mode = RecordingMode.Session;
        settings.Buffer.Enabled = true;
        settings.Buffer.Duration = TimeSpan.FromSeconds(45);
        settings.Audio.Tracks.Add(new AudioTrack { Name = "Game", Sources = { new AudioSource { Name = "Game audio", Kind = AudioSourceKind.Output } } });

        var resolved = SettingsResolver.Resolve(settings);

        Assert.IsType<ResolvedRecorderSettings>(resolved);
        Assert.Equal(RecordingMode.Session, resolved.Mode);
        Assert.True(resolved.BufferEnabled);
        Assert.Equal(TimeSpan.FromSeconds(45), resolved.BufferDuration);
        var track = Assert.Single(resolved.AudioTracks);
        Assert.Equal("Game", track.Name);
        var source = Assert.Single(track.Sources);
        Assert.Equal("Game audio", source.Name);
    }

    // The codec-and-quality choices reach the recorder through the same resolved shape as the encoder
    // id and the quality profile. They have no per-game override — the schema's per-game quality
    // override is resolution, fps, encoder and quality, and growing it is a separate decision — so the
    // global choice is the effective one for every game, including one that overrides other fields.
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

        // And they survive the clone the recorder takes of the config it is handed, so a caller
        // mutating its copy after the start cannot change how the running recording is encoded.
        var clone = resolved.Clone();
        Assert.Equal(RateControlMode.Cbr, clone.RateControl);
        Assert.Equal(22_000, clone.BitrateKbps);
        Assert.Equal(33_000, clone.MaxBitrateKbps);
    }

    // The default is constant quality on every machine: Cqp is the stored value, and the recorder
    // coerces it into x264's CRF when a software encoder is what the runtime resolved. The default
    // therefore records the user's intent rather than one family's spelling of it.
    [Fact]
    public void Resolve_DefaultsToConstantQuality()
    {
        var resolved = SettingsResolver.Resolve(new Settings());

        Assert.Equal(RateControlMode.Cqp, resolved.RateControl);
        Assert.Equal(15_000, resolved.BitrateKbps);
    }
}
