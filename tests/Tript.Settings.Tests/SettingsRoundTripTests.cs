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

        // The label only exists so a warning can name a monitor that is no longer attached, which
        // means it has to outlive the monitor being unplugged.
        Assert.Equal("Screen DP-1", reloaded.Capture.DisplayLabel);
        Assert.Equal(TimeSpan.FromSeconds(25), reloaded.Game.GameCaptureTimeout);
        Assert.Equal([@"C:\Tools\overlay.exe"], reloaded.Game.IgnoredApplications);
    }

    // The game-capture timeout crosses the JSON boundary as whole seconds: the settings UI sends a
    // number (that is the shape an UpdateSettings patch arrives in) and reads a number back off a
    // settings push. The TimeSpan string the serializer would otherwise demand is what made the
    // settings save fail, so the numeric form is pinned here.
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

    // A file written before the numeric seam carried an ISO 8601 duration: it still loads.
    [Fact]
    public void ALegacyTimeSpanStringGameCaptureTimeout_StillLoads()
    {
        File.WriteAllText(_provider.FilePath, """{"version":1,"game":{"gameCaptureTimeout":"00:01:35"}}""");

        var settings = _store.Load();
        Assert.Equal(TimeSpan.FromSeconds(95), settings.Game.GameCaptureTimeout);
    }

    // A hand-edited value the converter cannot use degrades to a non-positive timeout, which the
    // capture policy already maps to its default, rather than failing the whole file.
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

    // The trash retention is a stored setting with no UI yet, so the round trip is the only thing
    // holding it: a one-day default on a fresh model, and whatever the user set after a reload.
    [Fact]
    public void SaveThenLoad_RoundTripsTheTrashRetention()
    {
        Assert.Equal(24, _store.Load().Recording.TrashRetentionHours);

        _store.Load().Recording.TrashRetentionHours = 72;
        _store.Save();

        Assert.Equal(72, new SettingsStore(_provider).Load().Recording.TrashRetentionHours);

        // Zero is the "never purge" value and must survive too, rather than being read back as the
        // default because it is falsy.
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

    // The recording resolution must survive a round trip because it is read twice per launch and by
    // two different consumers: the host resets the OBS canvas to it at startup, and the recorder
    // scales the video encoder to it per recording. A resolution that reverted to the default on
    // every launch would silently change what every subsequent recording looks like.
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

    // The model's own default is 1080p, and it is reached without asking the platform anything. A
    // fresh install actually starts at the primary display's resolution, but that default is
    // applied by the host (Tript.App/Program.ApplyFirstRunDefaults) when it creates a settings file
    // that does not exist yet — deliberately not here.
    [Fact]
    public void TheDefaultResolution_IsTheSafeFallback_AndNeedsNoDisplay()
    {
        var settings = new Settings();

        Assert.Equal(1920, settings.Recording.ResolutionWidth);
        Assert.Equal(1080, settings.Recording.ResolutionHeight);
    }

    // An existing settings file keeps exactly the resolution it carries. This is the other half of
    // the first-run rule: the host applies a detected display size only when there is no file, so a
    // user who chose 1280x720 on a 1440p screen keeps 1280x720 — a load must never "correct" a stored
    // resolution towards the hardware.
    [Fact]
    public void ALoadOfAnExistingFile_KeepsItsStoredResolution()
    {
        File.WriteAllText(_provider.FilePath,
            """{"version":1,"recording":{"resolutionWidth":1280,"resolutionHeight":720}}""");

        var settings = _store.Load();
        Assert.Equal(1280, settings.Recording.ResolutionWidth);
        Assert.Equal(720, settings.Recording.ResolutionHeight);

        // And a save re-emits it rather than the model default.
        _store.Save();
        using var doc = JsonDocument.Parse(File.ReadAllText(_provider.FilePath));
        var recording = doc.RootElement.GetProperty("recording");
        Assert.Equal(1280, recording.GetProperty("resolutionWidth").GetInt32());
        Assert.Equal(720, recording.GetProperty("resolutionHeight").GetInt32());
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

    // The audio source's device selection is persisted by id so it survives a settings round trip:
    // the frontend saves a deviceId per source, and the recorder reads it back to attach the
    // capture source to that device. It is serialized as the camelCase "deviceId" key inside the
    // source object.
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

    // A source without a device selection reads back with a null DeviceId rather than a stale or
    // empty value, so a config that never chose a device keeps meaning "the platform default".
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

    // A file with no model fields at all still loads to defaults, and a save produces a valid
    // versioned file.
    [Fact]
    public void SaveWithoutLoad_WritesDefaults()
    {
        _store.Save();

        using var doc = JsonDocument.Parse(File.ReadAllText(_provider.FilePath));
        Assert.Equal(Settings.CurrentVersion, doc.RootElement.GetProperty("version").GetInt32());
    }
    // The settings file holds the recording directory, the game list and the audio routing. A plain
    // File.WriteAllText truncates before it writes, and a crash in that window leaves a blank file —
    // which loads as "no settings", i.e. every setting silently back to its default.
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

            // A reader racing the writer must never observe a truncated file. The rename is what
            // makes that true; assert the observable consequence rather than the mechanism.
            var stop = new ManualResetEventSlim();
            var blank = 0;
            var reader = new Thread(() =>
            {
                while (!stop.IsSet)
                {
                    // Read the way SettingsFileProvider reads (ReadWrite|Delete sharing): a plain
                    // ReadAllText holds the file without delete sharing and blocks the writer's
                    // replace-rename on Windows, which is a writer failure, not a reader failure.
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
                            // Lost the race against a rename mid-flight; the next loop turn reads
                            // the renamed-in file, which is the state the test is trying to pin.
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

    // A fixed "<path>.tmp" is shared by every concurrent writer of the same file, so two interleaved
    // write/rename pairs rename one writer's bytes over the other's.
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
