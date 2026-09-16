// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

public sealed class RecordingCanvasTests
{
    [Fact]
    public void TheCanvas_IsTheResolutionFromTheSettings()
    {
        var recording = new RecordingSettings { ResolutionWidth = 2560, ResolutionHeight = 1440 };

        var video = Program.BuildVideoSettings(recording);

        Assert.Equal(2560u, video.BaseWidth);
        Assert.Equal(1440u, video.BaseHeight);
    }

    [Fact]
    public void TheCanvas_IsNotScaledOnTheWayOut()
    {
        var video = Program.BuildVideoSettings(new RecordingSettings
        {
            ResolutionWidth = 3840,
            ResolutionHeight = 2160,
        });

        Assert.Equal(video.BaseWidth, video.OutputWidth);
        Assert.Equal(video.BaseHeight, video.OutputHeight);
        Assert.Equal(3840u, video.OutputWidth);
        Assert.Equal(2160u, video.OutputHeight);
    }

    [Fact]
    public void TheFrameRate_ComesFromTheSettingsAsAWholeFraction()
    {
        var video = Program.BuildVideoSettings(new RecordingSettings { Fps = 144 });

        Assert.Equal(144u, video.FpsNumerator);
        Assert.Equal(1u, video.FpsDenominator);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(-1, -1, -1)]
    public void ACorruptResolutionOrFrameRate_IsFlooredRatherThanRefusedByLibobs(int width, int height, int fps)
    {
        var video = Program.BuildVideoSettings(new RecordingSettings
        {
            ResolutionWidth = width,
            ResolutionHeight = height,
            Fps = fps,
        });

        Assert.Equal(1u, video.BaseWidth);
        Assert.Equal(1u, video.BaseHeight);
        Assert.Equal(1u, video.OutputWidth);
        Assert.Equal(1u, video.OutputHeight);
        Assert.Equal(1u, video.FpsNumerator);
    }

    [Fact]
    public void AFreshInstall_DefaultsToThePrimaryDisplaysResolution()
    {
        using var directory = new TemporarySettingsDirectory();
        var store = new SettingsStore(new SettingsFileProvider(directory.SettingsPath));

        var applied = Program.ApplyFirstRunDefaults(store, new DisplaySize(2560, 1440));

        Assert.True(applied);
        Assert.Equal(2560, store.Load().Recording.ResolutionWidth);
        Assert.Equal(1440, store.Load().Recording.ResolutionHeight);
    }

    [Fact]
    public void AFreshInstall_WithNoDetectedDisplay_KeepsTheSafeDefault()
    {
        using var directory = new TemporarySettingsDirectory();
        var store = new SettingsStore(new SettingsFileProvider(directory.SettingsPath));

        var applied = Program.ApplyFirstRunDefaults(store, null);

        Assert.True(applied);
        Assert.Equal(1920, store.Load().Recording.ResolutionWidth);
        Assert.Equal(1080, store.Load().Recording.ResolutionHeight);
        Assert.Equal(PrimaryDisplay.Fallback.Width, store.Load().Recording.ResolutionWidth);
        Assert.Equal(PrimaryDisplay.Fallback.Height, store.Load().Recording.ResolutionHeight);
    }

    [Fact]
    public void AFreshInstall_WithAnUnusableDetectedSize_KeepsTheSafeDefault()
    {
        using var directory = new TemporarySettingsDirectory();
        var store = new SettingsStore(new SettingsFileProvider(directory.SettingsPath));

        Program.ApplyFirstRunDefaults(store, new DisplaySize(0, 0));

        Assert.Equal(1920, store.Load().Recording.ResolutionWidth);
        Assert.Equal(1080, store.Load().Recording.ResolutionHeight);
    }

    [Fact]
    public void AnExistingSettingsFile_KeepsItsResolutionAndIsNotRewritten()
    {
        using var directory = new TemporarySettingsDirectory();
        File.WriteAllText(directory.SettingsPath,
            """{"version":1,"recording":{"resolutionWidth":1280,"resolutionHeight":720}}""");
        var written = File.ReadAllText(directory.SettingsPath);
        var store = new SettingsStore(new SettingsFileProvider(directory.SettingsPath));

        var applied = Program.ApplyFirstRunDefaults(store, new DisplaySize(2560, 1440));

        Assert.False(applied);
        Assert.Equal(1280, store.Load().Recording.ResolutionWidth);
        Assert.Equal(720, store.Load().Recording.ResolutionHeight);
        Assert.Equal(written, File.ReadAllText(directory.SettingsPath));
    }

    [Fact]
    public void AFreshInstall_WritesTheSettingsFile_SoASecondLaunchIsNotAFirstRun()
    {
        using var directory = new TemporarySettingsDirectory();
        var path = directory.SettingsPath;
        Program.ApplyFirstRunDefaults(new SettingsStore(new SettingsFileProvider(path)), new DisplaySize(2560, 1440));

        Assert.True(File.Exists(path));

        var second = new SettingsStore(new SettingsFileProvider(path));
        Assert.False(Program.ApplyFirstRunDefaults(second, new DisplaySize(1920, 1080)));
        Assert.Equal(2560, second.Load().Recording.ResolutionWidth);
        Assert.Equal(1440, second.Load().Recording.ResolutionHeight);
    }

    [Fact]
    public void AFreshInstallsCanvas_IsThePrimaryDisplaysResolution()
    {
        using var directory = new TemporarySettingsDirectory();
        var store = new SettingsStore(new SettingsFileProvider(directory.SettingsPath));
        Program.ApplyFirstRunDefaults(store, new DisplaySize(2560, 1440));

        var video = Program.BuildVideoSettings(store.Load().Recording);

        Assert.Equal(2560u, video.BaseWidth);
        Assert.Equal(1440u, video.BaseHeight);
    }

    [Fact]
    public void AFreshInstall_SeedsAMicAndDesktopTrack()
    {
        using var directory = new TemporarySettingsDirectory();
        var store = new SettingsStore(new SettingsFileProvider(directory.SettingsPath));

        Program.ApplyFirstRunDefaults(store, new DisplaySize(2560, 1440));

        var track = Assert.Single(store.Load().Audio.Tracks);
        Assert.Equal(2, track.Sources.Count);
        Assert.Contains(track.Sources, source => source.Kind == AudioSourceKind.Input && source.DeviceId is null);
        Assert.Contains(track.Sources, source => source.Kind == AudioSourceKind.Output && source.DeviceId is null);
    }

    [Fact]
    public void AnExistingSettingsFile_KeepsItsTracksAndIsNotSeeded()
    {
        using var directory = new TemporarySettingsDirectory();
        File.WriteAllText(directory.SettingsPath, """{"version":1,"audio":{"tracks":[]}}""");
        var store = new SettingsStore(new SettingsFileProvider(directory.SettingsPath));

        Program.ApplyFirstRunDefaults(store, new DisplaySize(2560, 1440));

        Assert.Empty(store.Load().Audio.Tracks);
    }

    private sealed class TemporarySettingsDirectory : IDisposable
    {
        private readonly string _directory;

        internal TemporarySettingsDirectory()
        {
            _directory = Path.Combine(Path.GetTempPath(), "tript-canvas-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        internal string SettingsPath => Path.Combine(_directory, "settings.json");

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
