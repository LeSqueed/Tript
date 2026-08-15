// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace Tript.Settings.Tests;

public class SettingsRoundTripTests : IDisposable
{
    private readonly string _dir;

    private readonly SettingsFileProvider _provider;

    private readonly SettingsStore _store;

    public SettingsRoundTripTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tript-settings-tests", Guid.NewGuid().ToString("N"));
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
            // Best-effort cleanup of the temp directory.
        }
    }

    [Fact]
    public void SaveThenLoad_RoundTripsTheModel()
    {
        var settings = _store.Load();
        settings.Recording.Mode = RecordingMode.Session;
        settings.Recording.Fps = 144;
        settings.Recording.Encoder = "nvenc";
        settings.Buffer.Enabled = true;
        settings.Buffer.Duration = TimeSpan.FromMinutes(2);
        settings.Audio.OutputMode = AudioOutputMode.Mute;
        settings.Capture.Method = DisplayCaptureMethod.Display;
        settings.Capture.Display = "DP-1";
        settings.Game.CaptureMode = GameCaptureMode.GameOnly;
        _store.Save();

        var reloaded = new SettingsStore(_provider).Load();

        Assert.Equal(RecordingMode.Session, reloaded.Recording.Mode);
        Assert.Equal(144, reloaded.Recording.Fps);
        Assert.Equal("nvenc", reloaded.Recording.Encoder);
        Assert.True(reloaded.Buffer.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(2), reloaded.Buffer.Duration);
        Assert.Equal(AudioOutputMode.Mute, reloaded.Audio.OutputMode);
        Assert.Equal(DisplayCaptureMethod.Display, reloaded.Capture.Method);
        Assert.Equal("DP-1", reloaded.Capture.Display);
        Assert.Equal(GameCaptureMode.GameOnly, reloaded.Game.CaptureMode);
    }

    // A build that models only part of the settings surface must not lose fields it does not
    // model. The file carries a top-level field this model does not know about; a save after a
    // load must re-emit it, and a load must not choke on it.
    [Fact]
    public void SaveAfterLoad_PreservesUnknownTopLevelKeys()
    {
        File.WriteAllText(_provider.FilePath, """{"version":1,"notes":"x","legacyField":42}""");

        var settings = _store.Load();
        Assert.Equal(42, settings.UnknownProperties["legacyField"].GetInt32());

        _store.Save();

        using var doc = JsonDocument.Parse(File.ReadAllText(_provider.FilePath));
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("legacyField", out var legacy), "unknown key was dropped on save");
        Assert.Equal(42, legacy.GetInt32());
        Assert.True(root.TryGetProperty("recording", out _));
    }

    // Same contract at the page level: a page this build does not fully model keeps its unknown
    // fields across a save.
    [Fact]
    public void SaveAfterLoad_PreservesUnknownPageKeys()
    {
        File.WriteAllText(_provider.FilePath, """{"recording":{"mode":"Session","futureSetting":"keep"}}""");

        var settings = _store.Load();
        Assert.Equal("keep", settings.Recording.UnknownProperties["futureSetting"].GetString());

        _store.Save();

        using var doc = JsonDocument.Parse(File.ReadAllText(_provider.FilePath));
        Assert.True(doc.RootElement.GetProperty("recording").TryGetProperty("futureSetting", out _),
            "unknown page key was dropped on save");
    }

    // A file with no model fields at all still loads to defaults, and a save produces a valid
    // versioned file.
    [Fact]
    public void SaveWithoutLoad_WritesDefaults()
    {
        _store.Save();

        using var doc = JsonDocument.Parse(File.ReadAllText(_provider.FilePath));
        Assert.Equal(Settings.CurrentVersion, doc.RootElement.GetProperty("version").GetInt32());
    }
}
