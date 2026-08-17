// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

// The two decisions that settle what a recording actually looks like, both taken during host startup
// and neither of them needing libobs to assert:
//
//   * the OBS canvas the runtime is reset with (Program.BuildVideoSettings), and
//   * the resolution a fresh install starts at (Program.ApplyFirstRunDefaults).
//
// The canvas is worth pinning because it was wrong in a way nothing failed on: it was a hardcoded
// 1920x1080 while the frame rate was read from the settings, and ObsRecorderSession.CreateOutput then
// asked the encoder to scale to the *configured* size. A user on 2560x1440 got a 1440p file upscaled
// from a 1080p canvas — every cost of recording 1440p and none of the detail, with no error anywhere.
// A test over the settings-to-canvas mapping is the cheap way that stays fixed.
//
// No app host and no runtime is started here, so this class stays out of the port-binding smoke
// collection.
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

    // Base and output are the same size on purpose: the encoder is separately told to emit
    // ResolutionWidth x ResolutionHeight (videoEncoder.SetScaledSize), so a canvas of any other size
    // means the scaler is resampling every frame. Equal sizes make it a no-op.
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

    // A zero in any dimension is the one combination obs_reset_video rejects outright, so a corrupt
    // or hand-edited settings file must not become a failure to start. Flooring at 1 records a
    // useless picture, which is still a running app the user can fix the setting in.
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

    // ---- the fresh-install resolution default ----
    //
    // The detected display is injected rather than read from the machine: the assertion is about the
    // rule, and a test that depended on the developer's monitors would pass or fail on hardware.

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

    // Detection failed (no display server, an X server without RandR, a platform we cannot read).
    // The model's own 1080p stands: a safe default beats a guess, and it is a resolution every
    // encoder on every machine can handle.
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

    // A size of zero is not a display — it is a platform answering with nothing. It is refused for
    // the same reason detection returning null is: the fallback is a working recording, a 0x0 canvas
    // is a host that will not start.
    [Fact]
    public void AFreshInstall_WithAnUnusableDetectedSize_KeepsTheSafeDefault()
    {
        using var directory = new TemporarySettingsDirectory();
        var store = new SettingsStore(new SettingsFileProvider(directory.SettingsPath));

        Program.ApplyFirstRunDefaults(store, new DisplaySize(0, 0));

        Assert.Equal(1920, store.Load().Recording.ResolutionWidth);
        Assert.Equal(1080, store.Load().Recording.ResolutionHeight);
    }

    // The rule that keeps the feature from being a data-loss bug: a settings file that exists is the
    // user's, whatever is in it. Someone who chose 1280x720 on a 1440p screen keeps 1280x720 on
    // every subsequent launch.
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
        // Untouched on disk as well: the first-run path does not even rewrite the file it declined
        // to change.
        Assert.Equal(written, File.ReadAllText(directory.SettingsPath));
    }

    // The first run is a first run exactly once. The file is written on the way out, so a second
    // launch takes the "existing file" path even if the user never opened the settings UI — and a
    // display swapped in later does not silently move the resolution out from under them.
    [Fact]
    public void AFreshInstall_WritesTheSettingsFile_SoASecondLaunchIsNotAFirstRun()
    {
        using var directory = new TemporarySettingsDirectory();
        var path = directory.SettingsPath;
        Program.ApplyFirstRunDefaults(new SettingsStore(new SettingsFileProvider(path)), new DisplaySize(2560, 1440));

        Assert.True(File.Exists(path));

        // The second launch sees a file and leaves it alone, even though a different display is
        // reported this time.
        var second = new SettingsStore(new SettingsFileProvider(path));
        Assert.False(Program.ApplyFirstRunDefaults(second, new DisplaySize(1920, 1080)));
        Assert.Equal(2560, second.Load().Recording.ResolutionWidth);
        Assert.Equal(1440, second.Load().Recording.ResolutionHeight);
    }

    // The canvas and the fresh-install default are the same number by construction: the host applies
    // the default first and then reads the settings back for the canvas (Program.BuildApp). If they
    // ever diverged the encoder would be scaling again, which is the bug this whole change is about.
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

    private sealed class TemporarySettingsDirectory : IDisposable
    {
        private readonly string _directory;

        internal TemporarySettingsDirectory()
        {
            _directory = Path.Combine(Path.GetTempPath(), "tript-canvas-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        // The file itself does not exist yet — that absence is what "fresh install" means here.
        internal string SettingsPath => Path.Combine(_directory, "settings.json");

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of the temp directory.
            }
        }
    }
}
