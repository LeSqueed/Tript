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
        }
    }

    [Fact]
    public void Streaming_DefaultsToOffWithTheTriptSender()
    {
        var streaming = _store.Load().Streaming;

        Assert.False(streaming.ShareEnabled);
        Assert.Equal(StreamShareWhen.WhileObsRuns, streaming.ShareWhen);
        Assert.Equal("Tript", streaming.SenderName);
    }

    [Fact]
    public void Streaming_RoundTrips()
    {
        var settings = _store.Load();
        settings.Streaming.ShareEnabled = true;
        settings.Streaming.ShareWhen = StreamShareWhen.Always;
        settings.Streaming.SenderName = "Tript Game";
        _store.Save();

        var reloaded = new SettingsStore(_provider).Load().Streaming;

        Assert.True(reloaded.ShareEnabled);
        Assert.Equal(StreamShareWhen.Always, reloaded.ShareWhen);
        Assert.Equal("Tript Game", reloaded.SenderName);
        using var onDisk = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "settings.json")));
        Assert.Equal("Always", onDisk.RootElement.GetProperty("streaming").GetProperty("shareWhen").GetString());
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
        settings.Capture.DisplayLabel = "Screen DP-1";
        settings.Game.GameCaptureTimeout = TimeSpan.FromSeconds(25);
        settings.Game.IgnoredApplications.Add(@"C:\Tools\overlay.exe");
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

        Assert.Equal("Screen DP-1", reloaded.Capture.DisplayLabel);
        Assert.Equal(TimeSpan.FromSeconds(25), reloaded.Game.GameCaptureTimeout);
        Assert.Equal([@"C:\Tools\overlay.exe"], reloaded.Game.IgnoredApplications);
    }

    [Fact]
    public void TheGameCaptureTimeout_IsPersistedAsWholeSeconds()
    {
        _store.Load().Game.GameCaptureTimeout = TimeSpan.FromSeconds(25);
        _store.Save();

        using var doc = JsonDocument.Parse(File.ReadAllText(_provider.FilePath));
        var value = doc.RootElement.GetProperty("game").GetProperty("gameCaptureTimeout");
        Assert.Equal(JsonValueKind.Number, value.ValueKind);
        Assert.Equal(25, value.GetDouble());
    }

    [Fact]
    public void ALegacyTimeSpanStringGameCaptureTimeout_StillLoads()
    {
        File.WriteAllText(_provider.FilePath, """{"version":1,"game":{"gameCaptureTimeout":"00:01:35"}}""");

        var settings = _store.Load();
        Assert.Equal(TimeSpan.FromSeconds(95), settings.Game.GameCaptureTimeout);
    }

    [Fact]
    public void TheBufferDuration_IsPersistedAsWholeSeconds()
    {
        _store.Load().Buffer.Duration = TimeSpan.FromSeconds(45);
        _store.Save();

        using var doc = JsonDocument.Parse(File.ReadAllText(_provider.FilePath));
        var value = doc.RootElement.GetProperty("buffer").GetProperty("duration");
        Assert.Equal(JsonValueKind.Number, value.ValueKind);
        Assert.Equal(45, value.GetDouble());
    }

    [Fact]
    public void ALegacyTimeSpanStringBufferDuration_StillLoads()
    {
        File.WriteAllText(_provider.FilePath, """{"version":1,"buffer":{"duration":"00:00:30"}}""");

        var settings = _store.Load();
        Assert.Equal(TimeSpan.FromSeconds(30), settings.Buffer.Duration);
    }

    [Fact]
    public void AnInvalidGameCaptureTimeoutNumber_ReadsBackNonPositive()
    {
        File.WriteAllText(_provider.FilePath, """{"version":1,"game":{"gameCaptureTimeout":-5}}""");
        Assert.True(_store.Load().Game.GameCaptureTimeout <= TimeSpan.Zero);

        File.WriteAllText(_provider.FilePath, """{"version":1,"game":{"gameCaptureTimeout":1e18}}""");
        Assert.True(_store.Load().Game.GameCaptureTimeout <= TimeSpan.Zero);
    }

    [Fact]
    public void TryUpdate_RejectionLeavesMemoryAndDiskUnchanged()
    {
        _store.Load().Recording.Fps = 60;
        _store.Save();
        var before = File.ReadAllText(_provider.FilePath);

        var saved = _store.TryUpdate(candidate =>
        {
            candidate.Recording.Fps = 144;
            return "rejected";
        }, out var settings, out var error);

        Assert.False(saved);
        Assert.Equal("rejected", error);
        Assert.Equal(60, settings.Recording.Fps);
        Assert.Equal(60, _store.Load().Recording.Fps);
        Assert.Equal(before, File.ReadAllText(_provider.FilePath));
    }

    [Fact]
    public void TryUpdate_CommitsTheCloneOnlyAfterWriting()
    {
        var original = _store.Load();

        var saved = _store.TryUpdate(candidate =>
        {
            candidate.Recording.Fps = 144;
            return null;
        }, out var settings, out var error);

        Assert.True(saved);
        Assert.Null(error);
        Assert.NotSame(original, settings);
        Assert.Equal(144, _store.Load().Recording.Fps);
        Assert.Equal(144, new SettingsStore(_provider).Load().Recording.Fps);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsGeneralSettings()
    {
        var settings = _store.Load();
        settings.General.StartWithWindows = true;
        settings.General.StartupVisibility = StartupVisibility.Tray;
        settings.General.MinimizeBehavior = MinimizeBehavior.Tray;
        settings.General.CloseBehavior = CloseBehavior.HideToTray;
        settings.General.Notifications.Enabled = false;
        settings.General.Notifications.Errors = false;
        _store.Save();

        var reloaded = new SettingsStore(_provider).Load();

        Assert.True(reloaded.General.StartWithWindows);
        Assert.Equal(StartupVisibility.Tray, reloaded.General.StartupVisibility);
        Assert.Equal(MinimizeBehavior.Tray, reloaded.General.MinimizeBehavior);
        Assert.Equal(CloseBehavior.HideToTray, reloaded.General.CloseBehavior);
        Assert.False(reloaded.General.Notifications.Enabled);
        Assert.False(reloaded.General.Notifications.Errors);
    }

    [Fact]
    public void AFileWithoutGeneralSettings_UsesTheGeneralDefaults()
    {
        File.WriteAllText(_provider.FilePath, "{\"version\":1,\"recording\":{\"mode\":\"Session\"}}");

        var settings = _store.Load();

        Assert.False(settings.General.StartWithWindows);
        Assert.Equal(StartupVisibility.Window, settings.General.StartupVisibility);
        Assert.Equal(MinimizeBehavior.Taskbar, settings.General.MinimizeBehavior);
        Assert.Equal(CloseBehavior.Exit, settings.General.CloseBehavior);
        Assert.True(settings.General.Notifications.Enabled);
    }

    [Fact]
    public void RemovedGeneralSettings_AreNotPreserved()
    {
        File.WriteAllText(_provider.FilePath,
            "{\"general\":{\"startupWindow\":\"settings\",\"closeButtonAction\":\"keepRecording\"}}");

        var settings = _store.Load();
        _store.Save();

        var general = JsonDocument.Parse(File.ReadAllText(_provider.FilePath)).RootElement.GetProperty("general");
        Assert.False(general.TryGetProperty("startupWindow", out _));
        Assert.False(general.TryGetProperty("closeButtonAction", out _));
        Assert.Equal(StartupVisibility.Window, settings.General.StartupVisibility);
        Assert.Equal(CloseBehavior.Exit, settings.General.CloseBehavior);
    }

    [Fact]
    public void AnUnknownGeneralEnum_FallsBackWithoutRejectingTheFile()
    {
        File.WriteAllText(_provider.FilePath,
            """{"general":{"startupVisibility":"FutureMode","closeBehavior":"FutureClose"}}""");

        var settings = _store.Load();

        Assert.Equal(StartupVisibility.Window, settings.General.StartupVisibility);
        Assert.Equal(CloseBehavior.Exit, settings.General.CloseBehavior);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsTheTrashRetention()
    {
        Assert.Equal(24, _store.Load().Recording.TrashRetentionHours);

        _store.Load().Recording.TrashRetentionHours = 72;
        _store.Save();

        Assert.Equal(72, new SettingsStore(_provider).Load().Recording.TrashRetentionHours);

        _store.Load().Recording.TrashRetentionHours = 0;
        _store.Save();

        Assert.Equal(0, new SettingsStore(_provider).Load().Recording.TrashRetentionHours);
    }

    [Fact]
    public void DeleteLinkedHighlightsByDefault_FreshAndOlderSettings_DefaultToFalse()
    {
        Assert.False(_store.Load().Recording.DeleteLinkedHighlightsByDefault);

        File.WriteAllText(_provider.FilePath, """{"version":1,"recording":{"mode":"Session"}}""");

        Assert.False(new SettingsStore(_provider).Load().Recording.DeleteLinkedHighlightsByDefault);
    }

    [Fact]
    public void DeleteLinkedHighlightsByDefault_TrueAndFalse_RoundTrip()
    {
        _store.Load().Recording.DeleteLinkedHighlightsByDefault = true;
        _store.Save();
        Assert.True(new SettingsStore(_provider).Load().Recording.DeleteLinkedHighlightsByDefault);

        _store.Load().Recording.DeleteLinkedHighlightsByDefault = false;
        _store.Save();
        Assert.False(new SettingsStore(_provider).Load().Recording.DeleteLinkedHighlightsByDefault);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsTheOutputDirectory()
    {
        var settings = _store.Load();
        settings.Recording.OutputDirectory = "/home/tester/Videos/Tript";
        _store.Save();

        var reloaded = new SettingsStore(_provider).Load();

        Assert.Equal("/home/tester/Videos/Tript", reloaded.Recording.OutputDirectory);
    }

    [Fact]
    public void SaveThenLoad_EmptyOutputDirectory_ReadsBackAsNull()
    {
        var settings = _store.Load();
        settings.Recording.OutputDirectory = null;
        _store.Save();

        var reloaded = new SettingsStore(_provider).Load();

        Assert.Null(reloaded.Recording.OutputDirectory);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsTheResolution()
    {
        var settings = _store.Load();
        settings.Recording.ResolutionWidth = 2560;
        settings.Recording.ResolutionHeight = 1440;
        _store.Save();

        var reloaded = new SettingsStore(_provider).Load();

        Assert.Equal(2560, reloaded.Recording.ResolutionWidth);
        Assert.Equal(1440, reloaded.Recording.ResolutionHeight);
    }

    [Fact]
    public void TheDefaultResolution_IsTheSafeFallback_AndNeedsNoDisplay()
    {
        var settings = new Settings();

        Assert.Equal(1920, settings.Recording.ResolutionWidth);
        Assert.Equal(1080, settings.Recording.ResolutionHeight);
    }

    [Fact]
    public void ALoadOfAnExistingFile_KeepsItsStoredResolution()
    {
        File.WriteAllText(_provider.FilePath,
            """{"version":1,"recording":{"resolutionWidth":1280,"resolutionHeight":720}}""");

        var settings = _store.Load();
        Assert.Equal(1280, settings.Recording.ResolutionWidth);
        Assert.Equal(720, settings.Recording.ResolutionHeight);

        _store.Save();
        using var doc = JsonDocument.Parse(File.ReadAllText(_provider.FilePath));
        var recording = doc.RootElement.GetProperty("recording");
        Assert.Equal(1280, recording.GetProperty("resolutionWidth").GetInt32());
        Assert.Equal(720, recording.GetProperty("resolutionHeight").GetInt32());
    }

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

    [Fact]
    public void SaveThenLoad_RoundTripsTheAudioSourceDeviceId()
    {
        const string deviceId = "\\\\?\\SWD\\MMDEVAPI\\{0.0.1.00000000}.{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}";
        var settings = _store.Load();
        settings.Audio.Tracks.Add(new AudioTrack
        {
            Name = "Game",
            Sources = { new AudioSource { Name = "Game audio", Kind = AudioSourceKind.Output, DeviceId = deviceId } },
        });
        _store.Save();

        using var doc = JsonDocument.Parse(File.ReadAllText(_provider.FilePath));
        var source = doc.RootElement.GetProperty("audio").GetProperty("tracks")[0].GetProperty("sources")[0];
        Assert.Equal(deviceId, source.GetProperty("deviceId").GetString());

        var reloaded = new SettingsStore(_provider).Load();
        var reloadedSource = Assert.Single(Assert.Single(reloaded.Audio.Tracks).Sources);
        Assert.Equal(deviceId, reloadedSource.DeviceId);
    }

    [Fact]
    public void SaveThenLoad_AllowsOneAudioDeviceOnMultipleTracks()
    {
        const string deviceId = "\\\\?\\SWD\\MMDEVAPI\\{0.0.1.00000000}.{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}";
        var settings = _store.Load();
        settings.Audio.Tracks.Add(new AudioTrack
        {
            Name = "Mic",
            Sources = { new AudioSource { Name = "Headset Mic", Kind = AudioSourceKind.Input, DeviceId = deviceId, Volume = 1.0f } },
        });
        settings.Audio.Tracks.Add(new AudioTrack
        {
            Name = "Mixed",
            Sources = { new AudioSource { Name = "Headset Mic", Kind = AudioSourceKind.Input, DeviceId = deviceId, Volume = 0.5f } },
        });
        _store.Save();

        var reloaded = new SettingsStore(_provider).Load();

        Assert.Equal(2, reloaded.Audio.Tracks.Count);
        Assert.All(reloaded.Audio.Tracks, track => Assert.Equal(deviceId, Assert.Single(track.Sources).DeviceId));
        Assert.Equal(1.0f, reloaded.Audio.Tracks[0].Sources[0].Volume);
        Assert.Equal(0.5f, reloaded.Audio.Tracks[1].Sources[0].Volume);
    }

    [Fact]
    public void SaveThenLoad_ASourceWithoutADevice_ReadsBackNull()
    {
        var settings = _store.Load();
        settings.Audio.Tracks.Add(new AudioTrack
        {
            Name = "Game",
            Sources = { new AudioSource { Name = "Game audio", Kind = AudioSourceKind.Output } },
        });
        _store.Save();

        var reloaded = new SettingsStore(_provider).Load();
        Assert.Null(Assert.Single(Assert.Single(reloaded.Audio.Tracks).Sources).DeviceId);
    }

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

    [Fact]
    public void ALoadOfAFileWithoutRateControl_TakesTheDefaults()
    {
        File.WriteAllText(_provider.FilePath, """{"version":1,"recording":{"mode":"Session","quality":10}}""");

        var settings = _store.Load();

        Assert.Equal(RateControlMode.Cqp, settings.Recording.RateControl);
        Assert.Equal(15_000, settings.Recording.BitrateKbps);
        Assert.Equal(0, settings.Recording.MaxBitrateKbps);
    }

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

    [Fact]
    public void SaveAfterLoad_PreservesUnknownGeneralAndNotificationKeys()
    {
        File.WriteAllText(_provider.FilePath,
            """{"general":{"futureSetting":"keep","notifications":{"futureNotification":true}}}""");

        var settings = _store.Load();
        Assert.Equal("keep", settings.General.UnknownProperties["futureSetting"].GetString());
        Assert.True(settings.General.Notifications.UnknownProperties["futureNotification"].GetBoolean());

        _store.Save();

        using var doc = JsonDocument.Parse(File.ReadAllText(_provider.FilePath));
        var general = doc.RootElement.GetProperty("general");
        Assert.True(general.TryGetProperty("futureSetting", out _));
        Assert.True(general.GetProperty("notifications").TryGetProperty("futureNotification", out _));
    }

    [Fact]
    public void SaveWithoutLoad_WritesDefaults()
    {
        _store.Save();

        using var doc = JsonDocument.Parse(File.ReadAllText(_provider.FilePath));
        Assert.Equal(Settings.CurrentVersion, doc.RootElement.GetProperty("version").GetInt32());
    }

    [Fact]
    public void Save_NeverLeavesTheSettingsFileBlank()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tript-settings-atomic", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new SettingsStore(new SettingsFileProvider(path));
            var settings = store.Load();
            settings.Recording.OutputDirectory = "/tmp/recordings";
            store.Save();

            var stop = new ManualResetEventSlim();
            var blank = 0;
            var reader = new Thread(() =>
            {
                while (!stop.IsSet)
                {
                    string text = "{}";
                    if (File.Exists(path))
                    {
                        try
                        {
                            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                FileShare.ReadWrite | FileShare.Delete);
                            using var reader = new StreamReader(stream);
                            text = reader.ReadToEnd();
                        }
                        catch (Exception ex) when (ex is IOException or FileNotFoundException or UnauthorizedAccessException)
                        {
                        }
                    }

                    if (string.IsNullOrWhiteSpace(text))
                        Interlocked.Increment(ref blank);
                }
            });
            reader.Start();

            for (var i = 0; i < 200; i++)
            {
                settings.Recording.OutputDirectory = $"/tmp/recordings-{i}";
                store.Save();
            }

            stop.Set();
            reader.Join();

            Assert.Equal(0, Volatile.Read(ref blank));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void AtomicFile_ReplacesTheTargetWithTheWrittenContents()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tript-atomic-temp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "record.json");
        try
        {
            AtomicFile.WriteAllText(path, "{\"a\":1}");

            Assert.Equal("{\"a\":1}", File.ReadAllText(path));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }
}
