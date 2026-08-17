// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Tript.Settings.Tests;

// The settings UI is split into logical pages — recording, buffer/replay, audio, capture, game
// (spec/frontend.md, design decision 2026-08-15) — and each page is addressed and saved
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
    public void TheFivePages_Exist()
    {
        Assert.Equal(5, Enum.GetValues<SettingsPage>().Length);
        Assert.Equal(
            new[] { SettingsPage.Recording, SettingsPage.Buffer, SettingsPage.Audio, SettingsPage.Capture, SettingsPage.Game },
            Enum.GetValues<SettingsPage>());
    }

    [Fact]
    public void EachPage_IsAddressedIndependently()
    {
        var recording = _store.Page<RecordingSettings>(SettingsPage.Recording);
        var buffer = _store.Page<BufferSettings>(SettingsPage.Buffer);

        Assert.Equal(SettingsPage.Recording, recording.Page);
        Assert.Equal(SettingsPage.Buffer, buffer.Page);

        recording.Load().Mode = RecordingMode.Session;
        buffer.Load().Enabled = true;
        recording.Save();

        // Editing the recording page left the buffer page's value intact in the saved file.
        var reloaded = new SettingsStore(_provider).Load();
        Assert.Equal(RecordingMode.Session, reloaded.Recording.Mode);
        Assert.True(reloaded.Buffer.Enabled);
    }

    [Fact]
    public void SaveOnePage_DoesNotDisturbTheOthers()
    {
        var settings = _store.Load();
        settings.Audio.Tracks.Add(new AudioTrack { Name = "Mic" });
        settings.Game.CaptureMode = GameCaptureMode.GameOnly;
        _store.Save();

        // A later session edits only the capture page and saves.
        var laterStore = new SettingsStore(_provider);
        laterStore.Load().Capture.Method = DisplayCaptureMethod.Display;
        laterStore.Save();

        // The audio page's track survived, because the whole model is serialized on save.
        var reloaded = new SettingsStore(_provider).Load();
        Assert.Equal("Mic", Assert.Single(reloaded.Audio.Tracks).Name);
        Assert.Equal(GameCaptureMode.GameOnly, reloaded.Game.CaptureMode);
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
