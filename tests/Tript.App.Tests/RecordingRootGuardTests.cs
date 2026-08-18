// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.App.Content;
using Tript.Core;
using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

// The guard on the recording directory. The content server checks no Origin — a media element sends
// none — so every byte under the effective root is readable by any page open in the user's browser.
// That is only tolerable while the root is a folder of recordings, which makes "which directory may
// become the root" a security boundary rather than a convenience check.
//
// The other half of this suite is the refusal path: a rejected directory must change NOTHING, or the
// stores and the content server end up disagreeing about where content lives.
public sealed class RecordingRootGuardTests : IDisposable
{
    private readonly string _contentRoot;
    private readonly string _settingsPath;
    private readonly SettingsStore _store;
    private readonly AppHost _host;

    public RecordingRootGuardTests()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), "tript-app-tests", nameof(RecordingRootGuardTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_contentRoot);
        _settingsPath = Path.Combine(_contentRoot, "settings.json");
        _store = new SettingsStore(new SettingsFileProvider(_settingsPath));

        _host = new AppHost(new AppOptions
        {
            ContentRoot = _contentRoot,
            SettingsPath = _settingsPath,
            WebRoot = _contentRoot,
            FakeRecorder = true,
        }, _store, runtime: null, new RecordingSessionTracker());
    }

    public void Dispose()
    {
        _host.Dispose();
        try
        {
            Directory.Delete(_contentRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---- what the guard classifies ----

    [Fact]
    public void AFilesystemRoot_IsRefused()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.NotNull(AppHost.UnsafeRecordingRoot(root));

        // With and without the trailing separator: both spell the same directory.
        Assert.NotNull(AppHost.UnsafeRecordingRoot(Path.TrimEndingDirectorySeparator(root)
            + Path.DirectorySeparatorChar));
    }

    [Theory]
    [InlineData("recordings")]
    [InlineData("./recordings")]
    [InlineData("../recordings")]
    public void ARelativePath_IsRefused(string candidate)
    {
        // A relative path resolves against whatever the process CWD happens to be, so the root the
        // content server serves from would depend on how the app was launched.
        Assert.NotNull(AppHost.UnsafeRecordingRoot(candidate));
    }

    [SkippableFact]
    public void TheUsersHomeDirectory_IsRefused()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Skip.If(string.IsNullOrEmpty(home), "this machine reports no home directory");

        Assert.NotNull(AppHost.UnsafeRecordingRoot(home));
    }

    [SkippableFact]
    public void AParentOfTheConfigDirectory_IsRefused()
    {
        // ~/.config on Linux, %AppData% on Windows: the directory Tript's own settings live under,
        // alongside every other application's.
        var parent = Path.GetDirectoryName(
            Path.TrimEndingDirectorySeparator(SettingsFilePaths.ConfigDirectory));
        Skip.If(string.IsNullOrEmpty(parent), "this machine reports no config directory");

        Assert.NotNull(AppHost.UnsafeRecordingRoot(parent));
        Assert.NotNull(AppHost.UnsafeRecordingRoot(SettingsFilePaths.ConfigDirectory));
    }

    // The case that must not regress. The guard refuses roots that CONTAIN somewhere sensitive; a
    // folder the user made for recordings is under the home directory and contains nothing, and
    // refusing it would break the ordinary setup for everyone.
    [SkippableFact]
    public void AnOrdinaryDirectoryUnderTheHomeDirectory_IsAccepted()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Skip.If(string.IsNullOrEmpty(home), "this machine reports no home directory");

        var candidate = Path.Combine(home, "Videos", "Tript-" + Guid.NewGuid().ToString("N"));

        Assert.Null(AppHost.UnsafeRecordingRoot(candidate));
    }

    [Fact]
    public void ADirectoryUnderTheTempRoot_IsAccepted()
    {
        // The path every test in this suite already uses, pinned so a future tightening of the guard
        // cannot quietly take the whole suite (and the app's default root) out with it.
        Assert.Null(AppHost.UnsafeRecordingRoot(_contentRoot));
    }

    // ---- the refusal changes nothing ----

    [Fact]
    public void ARefusedDirectory_LeavesTheEffectiveRootAndTheContentServerWhereTheyWere()
    {
        var before = _host.EffectiveRoot;
        Assert.Equal(before, _host.Content.ContentRoot);

        Assert.True(PointRootAt(Path.GetPathRoot(Path.GetTempPath())!));

        Assert.Equal(before, _host.EffectiveRoot);
        Assert.Equal(before, _host.Content.ContentRoot);
    }

    [Fact]
    public void ARefusedDirectory_LeavesEveryStorePointingAtTheOldRoot()
    {
        // A session under the current root, with the two stores' records keyed to it.
        var sessions = Path.Combine(_contentRoot, "sessions");
        Directory.CreateDirectory(sessions);
        File.WriteAllText(Path.Combine(sessions, "session-1.mp4"), "session");

        var clips = Path.Combine(_contentRoot, "clips");
        Directory.CreateDirectory(clips);
        File.WriteAllText(Path.Combine(clips, "session-1-clip-a.mp4"), "clip");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var refused = string.IsNullOrEmpty(home) ? Path.GetPathRoot(Path.GetTempPath())! : home;
        Assert.True(PointRootAt(refused));

        // The metadata store: a rename writes its record under the OLD root, not the refused one.
        _host.RenameContent(new RenameContentParameters
        {
            FileName = "sessions/session-1.mp4",
            ContentType = "recording",
            Title = "still here",
        });
        Assert.True(File.Exists(Path.Combine(_contentRoot, "metadata", "session-1.mp4.metadata.json")),
            "the metadata store must still be pointed at the root that was never replaced");

        // The clip title store, likewise.
        _host.RenameContent(new RenameContentParameters
        {
            FileName = "clips/session-1-clip-a.mp4",
            ContentType = "clip",
            Title = "also still here",
        });
        Assert.True(File.Exists(Path.Combine(_contentRoot, "metadata", "session-1-clip-a.mp4.title.json")),
            "the clip title store must still be pointed at the root that was never replaced");

        // The trash store: a delete lands in the old root's bin.
        _host.DeleteContent(new DeleteContentParameters { FileName = "sessions/session-1.mp4" });
        Assert.NotEmpty(_host.TrashEntries());
        Assert.True(Directory.Exists(Path.Combine(_contentRoot, ".trash")),
            "the trash store must still be pointed at the root that was never replaced");
    }

    // A refused directory must not be left in the settings file either: the settings push that
    // follows the refusal would show it as the recording folder, and the next launch resolves the
    // effective root straight from the file with no guard in front of it.
    [Fact]
    public void ARefusedDirectory_IsNotLeftInTheSettingsFile()
    {
        var accepted = Path.Combine(_contentRoot, "picked-recordings");
        Assert.True(PointRootAt(accepted));
        Assert.Equal(accepted, _store.Load().Recording.OutputDirectory);

        Assert.True(PointRootAt(Path.GetPathRoot(Path.GetTempPath())!));

        Assert.Equal(accepted, _store.Load().Recording.OutputDirectory);
        Assert.Equal(accepted, _host.EffectiveRoot);

        var onDisk = JsonDocument.Parse(File.ReadAllText(_settingsPath));
        Assert.Equal(accepted,
            onDisk.RootElement.GetProperty("recording").GetProperty("outputDirectory").GetString());
    }

    private bool PointRootAt(string directory) =>
        _host.UpdateSettings(JsonSerializer.SerializeToElement(new
        {
            recording = new { outputDirectory = directory },
        }));
}
