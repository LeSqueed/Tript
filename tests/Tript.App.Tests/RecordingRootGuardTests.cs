// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.App.Content;
using Tript.Core;
using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

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
        }, _store, runtime: null, new RecordingSessionTracker(),
            storageProbe: AmpleStorage.Probe);
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

    [Fact]
    public void AFilesystemRoot_IsRefused()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.NotNull(RecordingRootPolicy.WhyUnsafe(root));

        Assert.NotNull(RecordingRootPolicy.WhyUnsafe(Path.TrimEndingDirectorySeparator(root)
            + Path.DirectorySeparatorChar));
    }

    [Theory]
    [InlineData("recordings")]
    [InlineData("./recordings")]
    [InlineData("../recordings")]
    public void ARelativePath_IsRefused(string candidate)
    {
        Assert.NotNull(RecordingRootPolicy.WhyUnsafe(candidate));
    }

    [SkippableFact]
    public void TheUsersHomeDirectory_IsRefused()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Skip.If(string.IsNullOrEmpty(home), "this machine reports no home directory");

        Assert.NotNull(RecordingRootPolicy.WhyUnsafe(home));
    }

    [SkippableFact]
    public void AParentOfTheConfigDirectory_IsRefused()
    {
        var parent = Path.GetDirectoryName(
            Path.TrimEndingDirectorySeparator(SettingsFilePaths.ConfigDirectory));
        Skip.If(string.IsNullOrEmpty(parent), "this machine reports no config directory");

        Assert.NotNull(RecordingRootPolicy.WhyUnsafe(parent));
        Assert.NotNull(RecordingRootPolicy.WhyUnsafe(SettingsFilePaths.ConfigDirectory));
    }

    [SkippableFact]
    public void AnOrdinaryDirectoryUnderTheHomeDirectory_IsAccepted()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Skip.If(string.IsNullOrEmpty(home), "this machine reports no home directory");

        var candidate = Path.Combine(home, "Videos", "Tript-" + Guid.NewGuid().ToString("N"));

        Assert.Null(RecordingRootPolicy.WhyUnsafe(candidate));
    }

    [Fact]
    public void ADirectoryUnderTheTempRoot_IsAccepted()
    {
        Assert.Null(RecordingRootPolicy.WhyUnsafe(_contentRoot));
    }

    [Fact]
    public void Prepare_CreatesAnAcceptedDirectory()
    {
        var candidate = Path.Combine(_contentRoot, "prepared", "recordings");

        Assert.Null(RecordingRootPolicy.Prepare(candidate));
        Assert.True(Directory.Exists(candidate));
    }

    [Fact]
    public void Prepare_ExplainsARefusalWithoutCreatingAnything()
    {
        var refusal = RecordingRootPolicy.Prepare(Path.GetPathRoot(Path.GetTempPath())!);

        Assert.NotNull(refusal);
        Assert.StartsWith("the recording directory was refused because ", refusal);
    }

    [Fact]
    public void Resolve_FallsBackToTheContentRootWhenNothingIsConfigured()
    {
        var options = new AppOptions { ContentRoot = _contentRoot };

        Assert.Equal(Path.GetFullPath(_contentRoot), RecordingRootPolicy.Resolve(options, new Tript.Settings.Settings()));
    }

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
        var sessions = Path.Combine(_contentRoot, "sessions");
        Directory.CreateDirectory(sessions);
        File.WriteAllText(Path.Combine(sessions, "session-1.mp4"), "session");

        var clips = Path.Combine(_contentRoot, "clips");
        Directory.CreateDirectory(clips);
        File.WriteAllText(Path.Combine(clips, "session-1-clip-a.mp4"), "clip");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var refused = string.IsNullOrEmpty(home) ? Path.GetPathRoot(Path.GetTempPath())! : home;
        Assert.True(PointRootAt(refused));

        _host.RenameContent(new RenameContentParameters
        {
            FileName = "sessions/session-1.mp4",
            ContentType = "recording",
            Title = "still here",
        });
        Assert.True(File.Exists(Path.Combine(_contentRoot, "metadata", "session-1.mp4.metadata.json")),
            "the metadata store must still be pointed at the root that was never replaced");

        _host.RenameContent(new RenameContentParameters
        {
            FileName = "clips/session-1-clip-a.mp4",
            ContentType = "clip",
            Title = "also still here",
        });
        Assert.True(File.Exists(Path.Combine(_contentRoot, "metadata", "session-1-clip-a.mp4.title.json")),
            "the clip title store must still be pointed at the root that was never replaced");

        _host.DeleteContent(new DeleteContentParameters { FileName = "sessions/session-1.mp4" });
        Assert.NotEmpty(_host.TrashEntries());
        Assert.True(Directory.Exists(Path.Combine(_contentRoot, ".trash")),
            "the trash store must still be pointed at the root that was never replaced");
    }

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

    [Fact]
    public void AnUncreatableDirectory_IsNotCommitted()
    {
        var accepted = Path.Combine(_contentRoot, "picked-recordings");
        Assert.True(PointRootAt(accepted));
        var file = Path.Combine(_contentRoot, "not-a-directory");
        File.WriteAllText(file, "occupied");

        Assert.True(PointRootAt(file));

        Assert.Equal(accepted, _store.Load().Recording.OutputDirectory);
        Assert.Equal(accepted, _host.EffectiveRoot);
        using var onDisk = JsonDocument.Parse(File.ReadAllText(_settingsPath));
        Assert.Equal(accepted,
            onDisk.RootElement.GetProperty("recording").GetProperty("outputDirectory").GetString());
    }

    private bool PointRootAt(string directory) =>
        _host.UpdateSettings(JsonSerializer.SerializeToElement(new
        {
            recording = new { outputDirectory = directory },
        }));
}
