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

    // The recording page's output-directory setting must survive a full save/load round trip so a
    // user-chosen recording location persists across launches.
    [Fact]
    public void SaveThenLoad_RoundTripsTheOutputDirectory()
    {
        var settings = _store.Load();
        settings.Recording.OutputDirectory = "/home/tester/Videos/Tript";
        _store.Save();

        var reloaded = new SettingsStore(_provider).Load();

        Assert.Equal("/home/tester/Videos/Tript", reloaded.Recording.OutputDirectory);
    }

    // An empty output directory is the default ("use the platform default"): it is written as a
    // JSON null (WhenWritingNull) and reads back as null rather than a stale value.
    [Fact]
    public void SaveThenLoad_EmptyOutputDirectory_ReadsBackAsNull()
    {
        var settings = _store.Load();
        settings.Recording.OutputDirectory = null;
        _store.Save();

        var reloaded = new SettingsStore(_provider).Load();

        Assert.Null(reloaded.Recording.OutputDirectory);
    }

    // The recording page's codec-and-quality surface: the rate-control choice and the two bitrate
    // figures the rate-targeted modes use. These must survive a round trip for the same reason the
    // encoder id must — the recorder resolves them per machine, but the *choice* is the user's and is
    // persisted, and a mode that reverted to the default on every launch would silently change how
    // every subsequent recording is encoded.
    [Fact]
    public void SaveThenLoad_RoundTripsTheRateControlAndBitrateFields()
    {
        var settings = _store.Load();
        settings.Recording.RateControl = RateControlMode.Vbr;
        settings.Recording.BitrateKbps = 25_000;
        settings.Recording.MaxBitrateKbps = 40_000;
        _store.Save();

        var reloaded = new SettingsStore(_provider).Load();

        Assert.Equal(RateControlMode.Vbr, reloaded.Recording.RateControl);
        Assert.Equal(25_000, reloaded.Recording.BitrateKbps);
        Assert.Equal(40_000, reloaded.Recording.MaxBitrateKbps);
    }

    // The rate-control mode is persisted by name, not by ordinal: the member names are the
    // compatibility surface (SettingsSerialization registers JsonStringEnumConverter), so a member
    // added or reordered later cannot silently reinterpret an existing file as a different mode — and
    // a mode is exactly the value that must not be misread, since the recorder writes it into the
    // encoder's rate_control key.
    [Fact]
    public void TheRateControlMode_IsPersistedByName()
    {
        var settings = _store.Load();
        settings.Recording.RateControl = RateControlMode.Cbr;
        _store.Save();

        using var doc = JsonDocument.Parse(File.ReadAllText(_provider.FilePath));
        var recording = doc.RootElement.GetProperty("recording");
        Assert.Equal("Cbr", recording.GetProperty("rateControl").GetString());
    }

    // A settings file written before this build knew about rate control still loads, and the missing
    // fields take the model defaults — constant quality with the default bitrate, which is what the
    // recorder wrote for those files anyway.
    [Fact]
    public void ALoadOfAFileWithoutRateControl_TakesTheDefaults()
    {
        File.WriteAllText(_provider.FilePath, """{"version":1,"recording":{"mode":"Session","quality":10}}""");

        var settings = _store.Load();

        Assert.Equal(RateControlMode.Cqp, settings.Recording.RateControl);
        Assert.Equal(15_000, settings.Recording.BitrateKbps);
        Assert.Equal(0, settings.Recording.MaxBitrateKbps);
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
