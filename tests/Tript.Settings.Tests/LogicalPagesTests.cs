// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Tript.Settings.Tests;

public class LogicalPagesTests : IDisposable
{
    private readonly string _dir;

    private readonly SettingsFileProvider _provider;

    private readonly SettingsStore _store;

    public LogicalPagesTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tript-logical-pages-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _provider = new SettingsFileProvider(Path.Combine(_dir, "settings.json"));
        _store = new SettingsStore(_provider);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void TheNinePages_Exist()
    {
        Assert.Equal(9, Enum.GetValues<SettingsPage>().Length);
        Assert.Equal(
            new[]
            {
                SettingsPage.Recording, SettingsPage.Buffer, SettingsPage.Audio, SettingsPage.Capture,
                SettingsPage.Game, SettingsPage.General, SettingsPage.Hotkeys, SettingsPage.Streaming,
                SettingsPage.Storage,
            },
            Enum.GetValues<SettingsPage>());
    }

    [Fact]
    public void EachPage_IsAddressedIndependently()
    {
        var recording = _store.Page<RecordingSettings>(SettingsPage.Recording);
        var buffer = _store.Page<BufferSettings>(SettingsPage.Buffer);
        var general = _store.Page<GeneralSettings>(SettingsPage.General);
        var hotkeys = _store.Page<HotkeySettings>(SettingsPage.Hotkeys);

        Assert.Equal(SettingsPage.Recording, recording.Page);
        Assert.Equal(SettingsPage.Buffer, buffer.Page);
        Assert.Equal(SettingsPage.General, general.Page);
        Assert.Equal(SettingsPage.Hotkeys, hotkeys.Page);

        recording.Load().Mode = RecordingMode.Session;
        buffer.Load().Enabled = true;
        general.Load().StartupVisibility = StartupVisibility.Tray;
        hotkeys.Load().QuickClipSeconds = 15;
        recording.Save();

        var reloaded = new SettingsStore(_provider).Load();
        Assert.Equal(RecordingMode.Session, reloaded.Recording.Mode);
        Assert.True(reloaded.Buffer.Enabled);
        Assert.Equal(StartupVisibility.Tray, reloaded.General.StartupVisibility);
        Assert.Equal(15, reloaded.Hotkeys.QuickClipSeconds);
    }

    [Fact]
    public void AutomaticClips_DefaultOff_AndRoundTrips()
    {
        var settings = _store.Load();
        Assert.False(settings.Recording.AutomaticClipsEnabled);

        settings.Recording.AutomaticClipsEnabled = true;
        _store.Save();

        Assert.True(new SettingsStore(_provider).Load().Recording.AutomaticClipsEnabled);
    }

    [Fact]
    public void AutomaticClipSeconds_FreshSettings_DefaultToFiveBeforeAndEightAfter()
    {
        var settings = new SettingsStore(_provider).Load();
        Assert.Equal(5, settings.Recording.AutomaticClipBeforeSeconds);
        Assert.Equal(8, settings.Recording.AutomaticClipAfterSeconds);
    }

    [Fact]
    public void AutomaticClipSeconds_NonDefaultGlobalValues_RoundTrip()
    {
        var settings = _store.Load();
        settings.Recording.AutomaticClipBeforeSeconds = 3;
        settings.Recording.AutomaticClipAfterSeconds = 12;
        _store.Save();

        var reloaded = new SettingsStore(_provider).Load();
        Assert.Equal(3, reloaded.Recording.AutomaticClipBeforeSeconds);
        Assert.Equal(12, reloaded.Recording.AutomaticClipAfterSeconds);
    }

    [Fact]
    public void AutomaticClipOverride_WithBothFields_RoundTrips()
    {
        var settings = _store.Load();
        settings.Game.GameList.Add(new GameSetting
        {
            Id = "ow-clips-both",
            Name = "Overwatch Clips Both",
            AutomaticClipOverride = new GameAutomaticClipOverride { BeforeSeconds = 3, AfterSeconds = 12 },
        });
        _store.Save();

        var reloaded = new SettingsStore(_provider).Load();
        var game = Assert.Single(reloaded.Game.GameList, g => g.Id == "ow-clips-both");
        Assert.Equal(3, game.AutomaticClipOverride!.BeforeSeconds);
        Assert.Equal(12, game.AutomaticClipOverride!.AfterSeconds);
    }

    [Fact]
    public void AutomaticClipOverride_WithOnlyOneField_OtherStaysNullOnReload()
    {
        var settings = _store.Load();
        settings.Game.GameList.Add(new GameSetting
        {
            Id = "ow-clips-before",
            Name = "Overwatch Clips Before Only",
            AutomaticClipOverride = new GameAutomaticClipOverride { BeforeSeconds = 3 },
        });
        _store.Save();

        var reloaded = new SettingsStore(_provider).Load();
        var game = Assert.Single(reloaded.Game.GameList, g => g.Id == "ow-clips-before");
        Assert.Equal(3, game.AutomaticClipOverride!.BeforeSeconds);
        Assert.Null(game.AutomaticClipOverride.AfterSeconds);
    }

    [Fact]
    public void AutomaticClipOverride_AbsentGame_ReloadsAsNull()
    {
        var settings = _store.Load();
        settings.Game.GameList.Add(new GameSetting { Id = "ow-no-clips", Name = "Overwatch No Clips" });
        _store.Save();

        var reloaded = new SettingsStore(_provider).Load();
        var game = Assert.Single(reloaded.Game.GameList, g => g.Id == "ow-no-clips");
        Assert.Null(game.AutomaticClipOverride);
    }

    [Fact]
    public void SaveOnePage_DoesNotDisturbTheOthers()
    {
        var settings = _store.Load();
        settings.Audio.Tracks.Add(new AudioTrack { Name = "Mic" });
        settings.Game.GameCaptureTimeout = TimeSpan.FromSeconds(25);
        _store.Save();

        var laterStore = new SettingsStore(_provider);
        laterStore.Load().Capture.Method = DisplayCaptureMethod.Display;
        laterStore.Save();

        var reloaded = new SettingsStore(_provider).Load();
        Assert.Equal("Mic", Assert.Single(reloaded.Audio.Tracks).Name);
        Assert.Equal(TimeSpan.FromSeconds(25), reloaded.Game.GameCaptureTimeout);
        Assert.Equal(DisplayCaptureMethod.Display, reloaded.Capture.Method);
    }

    [Fact]
    public void GamePage_HoldsPerGameOverrides()
    {
        var settings = _store.Load();
        settings.Game.GameList.Add(new GameSetting
        {
            Id = "ow",
            Name = "Overwatch",
            QualityOverride = new GameQualityOverride { Fps = 240 },
        });
        _store.Save();

        var reloaded = new SettingsStore(_provider).Load();
        var game = Assert.Single(reloaded.Game.GameList, g => g.Id == "ow");
        Assert.Equal("ow", game.Id);
        Assert.Equal(240, game.QualityOverride!.Fps);
    }
}
