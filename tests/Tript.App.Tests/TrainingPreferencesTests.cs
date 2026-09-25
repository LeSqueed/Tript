// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

#if TRIPT_TRAINING

using Tript.App.Training;
using Tript.Core;
using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

public sealed class TrainingPreferencesTests
{
    [Fact]
    public void SavePreferences_round_trips_through_the_workspace_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "tript-training-prefs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var workspace = TrainingWorkspace.AtRoot("Overwatch", root);
            Assert.Null(workspace.LoadPreferences());

            workspace.SavePreferences(new TrainingPreferences
            {
                Epochs = 50,
                Device = "rocm",
                AugmentCopies = 4,
            });

            var loaded = workspace.LoadPreferences();
            Assert.NotNull(loaded);
            Assert.Equal(50, loaded!.Epochs);
            Assert.Equal("rocm", loaded.Device);
            Assert.Equal(4, loaded.AugmentCopies);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LoadPreferences_ignores_a_corrupted_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "tript-training-prefs-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var workspace = TrainingWorkspace.AtRoot("Overwatch", root);
            File.WriteAllText(workspace.PreferencesPath, "{ not json ");

            Assert.Null(workspace.LoadPreferences());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Revision_ignores_the_preferences_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "tript-training-prefs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var workspace = TrainingWorkspace.AtRoot("Overwatch", root);
            Directory.CreateDirectory(workspace.RootPath);
            File.WriteAllText(workspace.EventsPath, "[]");

            var before = workspace.Revision();
            workspace.SavePreferences(new TrainingPreferences
            {
                Epochs = 50,
                Device = "cuda",
                AugmentCopies = 2,
            });
            Assert.Equal(before, workspace.Revision());

            File.WriteAllText(workspace.EventsPath, "[1]");
            Assert.NotEqual(before, workspace.Revision());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Invalid_training_options_are_rejected_without_persisting_preferences()
    {
        var gameId = "invalid-options-" + Guid.NewGuid().ToString("N");
        var workspace = TrainingWorkspace.ForGame(gameId);
        using var fixture = new HostFixture();
        try
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Host.StartTraining(
                new StartTrainingParameters { GameId = gameId, Epochs = 0, AugmentCopies = 0 }));
            Assert.Null(workspace.LoadPreferences());

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Host.StartTraining(
                new StartTrainingParameters { GameId = gameId, Epochs = 1, AugmentCopies = -1 }));
            Assert.Null(workspace.LoadPreferences());
        }
        finally
        {
            if (Directory.Exists(workspace.RootPath)) Directory.Delete(workspace.RootPath, recursive: true);
        }
    }

    [Fact]
    public async Task Blocked_training_request_does_not_replace_preferences()
    {
        var gameId = "blocked-options-" + Guid.NewGuid().ToString("N");
        var workspace = TrainingWorkspace.ForGame(gameId);
        workspace.SavePreferences(new TrainingPreferences
        {
            Epochs = 25,
            Device = "cpu",
            AugmentCopies = 2,
        });
        using var fixture = new HostFixture();
        var session = (TrainingSession)typeof(AppHost).GetField("_training",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(fixture.Host)!;
        var active = session.Begin("another-game", "training", () => { });
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Host.StartTraining(
                new StartTrainingParameters
                {
                    GameId = gameId,
                    Epochs = 80,
                    Device = "cuda",
                    AugmentCopies = 8,
                }));

            var preferences = Assert.IsType<TrainingPreferences>(workspace.LoadPreferences());
            Assert.Equal(25, preferences.Epochs);
            Assert.Equal("cpu", preferences.Device);
            Assert.Equal(2, preferences.AugmentCopies);
        }
        finally
        {
            session.End(active);
            if (Directory.Exists(workspace.RootPath)) Directory.Delete(workspace.RootPath, recursive: true);
        }
    }

    private sealed class HostFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            "tript-training-start-" + Guid.NewGuid().ToString("N"));

        internal HostFixture()
        {
            Directory.CreateDirectory(_root);
            var settings = new SettingsStore(new SettingsFileProvider(Path.Combine(_root, "settings.json")));
            settings.Load();
            settings.Save();
            Host = new AppHost(new AppOptions
            {
                ContentRoot = _root,
                SettingsPath = Path.Combine(_root, "settings.json"),
                WebRoot = _root,
                FakeRecorder = true,
                ControlPort = 0,
                UiPort = 0,
                ContentPort = 0,
            }, settings, runtime: null, new RecordingSessionTracker(),
            storageProbe: AmpleStorage.Probe);
        }

        internal AppHost Host { get; }

        public void Dispose()
        {
            Host.Dispose();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

#endif
