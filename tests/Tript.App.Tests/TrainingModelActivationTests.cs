// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

#if TRIPT_TRAINING

using System.Reflection;
using Tript.App.Content;
using Tript.App.Training;
using Tript.Core;
using Tript.Detection;
using Tript.GameDiscovery;
using Tript.Obs;
using Tript.Recorder;
using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

public sealed class TrainingModelActivationTests
{
    [Fact]
    public void Successful_activation_publishes_the_active_model()
    {
        var errors = 0;
        var statePushes = 0;

        var assignments = 0;
        AppHost.ActivateRecordingModelCore("requested", null, _ => true, () => assignments++,
            () => errors++, () => statePushes++);

        Assert.Equal(0, errors);
        Assert.Equal(1, assignments);
        Assert.Equal(1, statePushes);
    }

    [Fact]
    public void Training_install_restarts_the_manually_active_model_not_the_recording_game()
    {
        Assert.True(AppHost.ShouldRestartDetectionForModelInstall(
            isRecording: true, activeDetectionGameId: "manually-active", installedGameId: "manually-active"));
        Assert.False(AppHost.ShouldRestartDetectionForModelInstall(
            isRecording: true, activeDetectionGameId: "manually-active", installedGameId: "recording-game"));
        Assert.False(AppHost.ShouldRestartDetectionForModelInstall(
            isRecording: false, activeDetectionGameId: "manually-active", installedGameId: "manually-active"));
    }

    [Fact]
    public void Failed_activation_and_restoration_still_push_the_cleared_active_model()
    {
        string? activeGameId = "previous";
        string? pushedGameId = "not-pushed";
        var attempts = new List<string>();
        var errors = 0;

        bool StartDetection(string gameId)
        {
            attempts.Add(gameId);
            activeGameId = null;
            return false;
        }

        var assignments = 0;
        AppHost.ActivateRecordingModelCore("requested", "previous", StartDetection, () => assignments++,
            () => errors++, () => pushedGameId = activeGameId);

        Assert.Equal(["requested", "previous"], attempts);
        Assert.Equal(1, errors);
        Assert.Equal(0, assignments);
        Assert.Null(pushedGameId);
    }

    [Fact]
    public void Activation_exception_restores_the_previous_model_and_reports_the_failure()
    {
        var attempts = new List<string>();
        var errors = 0;
        var statePushes = 0;

        var assignments = 0;
        AppHost.ActivateRecordingModelCore("requested", "previous", gameId =>
        {
            attempts.Add(gameId);
            if (gameId == "requested") throw new InvalidDataException("bad model");
            return true;
        }, () => assignments++, () => errors++, () => statePushes++);

        Assert.Equal(["requested", "previous"], attempts);
        Assert.Equal(1, errors);
        Assert.Equal(0, assignments);
        Assert.Equal(1, statePushes);
    }

    [Fact]
    public void Selected_model_moves_the_session_and_linked_automatic_highlights_on_stop()
    {
        var root = Path.Combine(Path.GetTempPath(), "tript-model-session-move-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settingsPath = Path.Combine(root, "settings.json");
        var store = new SettingsStore(new SettingsFileProvider(settingsPath));
        var settings = store.Load();
        settings.Game.GameList =
        [
            new GameSetting { Id = "source-game", Name = "Source Game" },
            new GameSetting { Id = "target-game", Name = "Target Game" },
        ];
        store.Save();
        var tracker = new RecordingSessionTracker();
        var host = new AppHost(new AppOptions
        {
            ContentRoot = root,
            SettingsPath = settingsPath,
            WebRoot = root,
            FakeRecorder = true,
        }, store, runtime: null, tracker);

        try
        {
            host.TrackDetectedGameStarted(new DetectedGameProcess("source-game", 4001, "Source Game",
                @"C:\Games\Source\source.exe"));
            Assert.True(host.StartRecording(null));
            var sourcePath = (string)typeof(AppHost).GetField("_activeOutputPath",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            File.WriteAllText(sourcePath, "session");
            var sourceRelative = Path.GetRelativePath(root, sourcePath).Replace(Path.DirectorySeparatorChar, '/');
            var highlightName = "linked-highlight.mp4";
            var sourceHighlight = Path.Combine(root, "Source Game", "highlights", highlightName);
            Directory.CreateDirectory(Path.GetDirectoryName(sourceHighlight)!);
            File.WriteAllText(sourceHighlight, "highlight");

            var pending = (RecordingMetadata)typeof(AppHost).GetField("_pendingMetadata",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            pending.Title = "Final match";
            pending.Favorite = true;
            pending.DurationSeconds = 42;
            pending.Compressed = true;
            pending.AudioTracks = [new AudioTrackLayout { Index = 1, Name = "Game" }];
            tracker.Active!.AddBookmark(new Bookmark
            {
                Type = BookmarkType.Kill,
                Time = TimeSpan.FromSeconds(12),
            });

            var clips = (ClipTitleStore)typeof(AppHost).GetField("_clipTitles",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            Assert.True(clips.Save(highlightName, "Clutch"));
            Assert.True(clips.SaveFavorite(highlightName, true));
            Assert.True(clips.SaveDuration(highlightName, 10));
            Assert.True(clips.SaveHdrStatus(highlightName, true));
            Assert.True(clips.SaveAutomatic(highlightName, sourceRelative, 8, 18));
            Assert.True(clips.SaveGame(highlightName, "Source Game", "source-game"));

            typeof(AppHost).GetMethod("AssignActiveRecordingToGame",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, ["target-game"]);

            Assert.Equal("target-game", host.CurrentGameId);
            Assert.True(host.StopRecording());

            var targetSession = Path.Combine(root, "Target Game", "sessions", Path.GetFileName(sourcePath));
            var targetHighlight = Path.Combine(root, "Target Game", "highlights", highlightName);
            Assert.False(File.Exists(sourcePath));
            Assert.False(File.Exists(sourceHighlight));
            Assert.True(File.Exists(targetSession));
            Assert.True(File.Exists(targetHighlight));

            var metadata = ((RecordingMetadataStore)typeof(AppHost).GetField("_metadata",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!).Load(Path.GetFileName(sourcePath));
            Assert.NotNull(metadata);
            Assert.Equal("Target Game/sessions/" + Path.GetFileName(sourcePath), metadata.VideoPath);
            Assert.Equal("Target Game", metadata.Game);
            Assert.Equal("target-game", metadata.GameId);
            Assert.Equal("Final match", metadata.Title);
            Assert.True(metadata.Favorite);
            Assert.Equal(42, metadata.DurationSeconds);
            Assert.True(metadata.Compressed);
            Assert.Single(metadata.AudioTracks);
            Assert.Single(metadata.Bookmarks);

            var clip = clips.LoadRecord(highlightName);
            Assert.NotNull(clip);
            Assert.Equal(metadata.VideoPath, clip.SourceSessionPath);
            Assert.Equal("Target Game", clip.Game);
            Assert.Equal("target-game", clip.GameId);
            Assert.Equal("Clutch", clip.Title);
            Assert.True(clip.Favorite);
            Assert.Equal(10, clip.DurationSeconds);
            Assert.True(clip.IsHdr);
            Assert.Equal(8, clip.ClipStartTime);
            Assert.Equal(18, clip.ClipEndTime);
        }
        finally
        {
            host.StopRecording();
            host.Dispose();
            DeleteRecursively(root);
        }
    }

    [Fact]
    public void Installing_over_the_manually_active_model_stops_and_restores_detection()
    {
        var gameId = "install-active-" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "tript-training-install-" + Guid.NewGuid().ToString("N"));
        var modelRoot = Path.Combine(root, "models");
        var bundle = Path.Combine(modelRoot, gameId);
        Directory.CreateDirectory(bundle);
        var shipped = Path.Combine(AppContext.BaseDirectory, "data", "models",
            "57ZZVAZ0PJK8VQGPKB728QE57C");
        Assert.True(File.Exists(Path.Combine(shipped, "model.onnx")), $"Missing test model at {shipped}");
        File.Copy(Path.Combine(shipped, "model.onnx"), Path.Combine(bundle, "model.onnx"));
        File.Copy(Path.Combine(shipped, "events.json"), Path.Combine(bundle, "events.json"));

        var workspace = TrainingWorkspace.ForGame(gameId);
        var installedRoot = TrainingWorkspace.ForGame(gameId, TrainingPaths.InstalledModelsPath).RootPath;
        Directory.CreateDirectory(workspace.DatasetPath);
        File.Copy(Path.Combine(bundle, "events.json"), workspace.EventsPath);
        var modelSource = Path.Combine(workspace.DatasetPath, "model.onnx");
        File.Copy(Path.Combine(bundle, "model.onnx"), modelSource);

        var settingsPath = Path.Combine(root, "settings.json");
        var store = new SettingsStore(new SettingsFileProvider(settingsPath));
        store.Load();
        store.Save();
        var host = new AppHost(new AppOptions
        {
            ContentRoot = root,
            SettingsPath = settingsPath,
            WebRoot = root,
            FakeRecorder = true,
        }, store, runtime: null, new RecordingSessionTracker());
        ModelService.ConfigureModelRoots(modelRoot);

        FrameSourceRegistry.SetResolver(() => new InertFrameSource());
        try
        {
            Assert.True(host.StartRecording("recording-game-" + Guid.NewGuid().ToString("N")));
            host.ActivateRecordingModel(gameId);
            Assert.Equal(gameId, ActiveDetectionGameId(host));
            Assert.Equal(gameId, host.CurrentGameId);

            Assert.Throws<InvalidOperationException>(() => ModelService.InvalidateModel(gameId));

            var result = host.InstallTrainingModel(gameId, modelSource);

            Assert.True(File.Exists(result.ModelPath));
            Assert.Equal(File.ReadAllBytes(modelSource), File.ReadAllBytes(result.ModelPath));

            Assert.Equal(gameId, ActiveDetectionGameId(host));
        }
        finally
        {
            host.StopRecording();
            host.Dispose();
            FrameSourceRegistry.Reset();
            ModelService.ConfigureUserModelRoot(ModelService.BasePath);
            DeleteRecursively(installedRoot);
            DeleteRecursively(workspace.RootPath);
            DeleteRecursively(root);
        }
    }

    private sealed class InertFrameSource : IFrameSource
    {
        private sealed class InertSubscription : IFrameSubscription
        {
            public void Dispose()
            {
            }
        }

        public IFrameSubscription Subscribe(FramePixelFormat format, int width, int height,
            FrameCallback callback, uint frameRateDivisor) => new InertSubscription();

        public VideoTiming? GetVideoTiming() => null;
    }

    private static string? ActiveDetectionGameId(AppHost host) =>
        (string?)typeof(AppHost).GetField("_activeDetectionGameId",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host);

    private static void DeleteRecursively(string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

#endif
