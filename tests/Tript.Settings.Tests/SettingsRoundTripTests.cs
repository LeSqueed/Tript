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
    public void SaveThenLoad_EveryPageKeepsItsNonDefaultValues()
    {
        var settings = _store.Load();
        var recording = settings.Recording;
        recording.Mode = RecordingMode.Session;
        recording.ResolutionWidth = 2560;
        recording.ResolutionHeight = 1440;
        recording.Fps = 144;
        recording.Encoder = "nvenc";
        recording.Quality = 18;
        recording.RateControl = RateControlMode.Vbr;
        recording.BitrateKbps = 25_000;
        recording.MaxBitrateKbps = 40_000;
        recording.EnableHdr = false;
        recording.OutputDirectory = "/home/tester/Videos/Tript";
        recording.AutomaticClipsEnabled = true;
        recording.AutomaticClipBeforeSeconds = 3;
        recording.AutomaticClipAfterSeconds = 12;
        recording.DeleteLinkedHighlightsByDefault = true;
        recording.TrashRetentionHours = 0;
        settings.Buffer.Enabled = true;
        settings.Buffer.Duration = TimeSpan.FromMinutes(2);
        settings.Buffer.MaxSizeBytes = 1024;
        settings.Audio.OutputMode = AudioOutputMode.Mute;
        settings.Audio.Tracks.Add(new AudioTrack
        {
            Name = "Game",
            Sources =
            {
                new AudioSource
                {
                    Name = "Game audio", Kind = AudioSourceKind.Output, Volume = 0.5f,
                    DeviceId = @"\\?\SWD\MMDEVAPI\{0.0.1.00000000}.{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}",
                },
            },
        });
        settings.Capture.Method = DisplayCaptureMethod.Display;
        settings.Capture.Display = "DP-1";
        settings.Capture.DisplayLabel = "Screen DP-1";
        settings.Game.GameCaptureTimeout = TimeSpan.FromSeconds(25);
        settings.Game.AutoRecordDetectedGames = false;
        settings.Game.IgnoredApplications.Add(@"C:\Tools\overlay.exe");
        settings.Game.GameList.Add(new GameSetting
        {
            Id = "ow",
            Name = "Overwatch",
            QualityOverride = new GameQualityOverride { Fps = 240 },
            AutomaticClipOverride = new GameAutomaticClipOverride { BeforeSeconds = 3, AfterSeconds = 12 },
        });
        settings.General.StartWithWindows = true;
        settings.General.StartupVisibility = StartupVisibility.Tray;
        settings.General.MinimizeBehavior = MinimizeBehavior.Tray;
        settings.General.CloseBehavior = CloseBehavior.HideToTray;
        settings.General.ConvertHdrClipsToSdr = true;
        settings.General.CheckForUpdatesAutomatically = false;
        settings.General.DebugLogging = true;
        settings.General.ClipOutputMode = ClipOutputMode.Separate;
        settings.General.Notifications.Enabled = false;
        settings.General.Notifications.ErrorsSound = false;
        settings.Streaming.ShareEnabled = true;
        settings.Streaming.ShareWhen = StreamShareWhen.Always;
        settings.Streaming.SenderName = "Tript Game";
        settings.Storage.MinimumFreeBytes = 1;
        settings.Storage.WhenFull = StorageFullAction.ReclaimOldest;
        settings.Storage.PolicyConfirmed = true;
        settings.Storage.KeepSharingWhenFull = false;
        _store.Save();

        Assert.Equivalent(settings, new SettingsStore(_provider).Load(), strict: true);
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
