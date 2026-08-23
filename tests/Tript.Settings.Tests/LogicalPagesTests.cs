// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Tript.Settings.Tests;

// The settings UI is split into logical pages — general, recording, buffer/replay, audio, capture, game
// — and each page is addressed and saved
// independently. These tests pin that the five pages exist, that each persists its own fields,
// and that editing one page does not disturb another.
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
    public void TheSixPages_Exist()
    {
        Assert.Equal(6, Enum.GetValues<SettingsPage>().Length);
        Assert.Equal(
            new[] { SettingsPage.Recording, SettingsPage.Buffer, SettingsPage.Audio, SettingsPage.Capture, SettingsPage.Game, SettingsPage.General },
            Enum.GetValues<SettingsPage>());
    }

    [Fact]
    public void EachPage_IsAddressedIndependently()
    {
        var recording = _store.Page<RecordingSettings>(SettingsPage.Recording);
        var buffer = _store.Page<BufferSettings>(SettingsPage.Buffer);
        var general = _store.Page<GeneralSettings>(SettingsPage.General);

        Assert.Equal(SettingsPage.Recording, recording.Page);
        Assert.Equal(SettingsPage.Buffer, buffer.Page);
        Assert.Equal(SettingsPage.General, general.Page);

        recording.Load().Mode = RecordingMode.Session;
        buffer.Load().Enabled = true;
        general.Load().StartupVisibility = StartupVisibility.Tray;
        recording.Save();

        // Editing the recording page left the buffer page's value intact in the saved file.
        var reloaded = new SettingsStore(_provider).Load();
        Assert.Equal(RecordingMode.Session, reloaded.Recording.Mode);
        Assert.True(reloaded.Buffer.Enabled);
        Assert.Equal(StartupVisibility.Tray, reloaded.General.StartupVisibility);
    }

    [Fact]
    public void SaveOnePage_DoesNotDisturbTheOthers()
    {
        var settings = _store.Load();
        settings.Audio.Tracks.Add(new AudioTrack { Name = "Mic" });
        settings.Game.GameCaptureTimeout = TimeSpan.FromSeconds(25);
        _store.Save();

        // A later session edits only the capture page and saves.
        var laterStore = new SettingsStore(_provider);
        laterStore.Load().Capture.Method = DisplayCaptureMethod.Display;
        laterStore.Save();

        // The audio page's track survived, because the whole model is serialized on save.
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
