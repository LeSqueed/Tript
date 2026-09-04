// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

#if TRIPT_TRAINING

using System.Reflection;
using Tript.App.Training;
using Tript.Core;
using Tript.Detection;
using Tript.Obs;
using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

public sealed class TrainingModelActivationTests
{
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

        AppHost.ActivateRecordingModelCore("requested", "previous", StartDetection,
            () => errors++, () => pushedGameId = activeGameId);

        Assert.Equal(["requested", "previous"], attempts);
        Assert.Equal(1, errors);
        Assert.Null(pushedGameId);
    }

    // Manual activation lets the game whose model runs differ from the game being recorded. An
    // install/replace over that model must stop the ACTIVE session (the one holding the model
    // refcount), invalidate, install, and restore detection to the activated game — not to the
    // recording game. Before the fix the install keyed off the recording game, so the detector
    // kept its reference and InvalidateModel failed with "still in use".
    [Fact]
    public void Installing_over_the_manually_active_model_stops_and_restores_detection()
    {
        var gameId = "install-active-" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "tript-training-install-" + Guid.NewGuid().ToString("N"));
        var modelRoot = Path.Combine(root, "models");
        var bundle = Path.Combine(modelRoot, gameId);
        Directory.CreateDirectory(bundle);
        var shipped = Path.Combine(AppContext.BaseDirectory, "data", "models", "Overwatch");
        Assert.True(File.Exists(Path.Combine(shipped, "model.onnx")), $"Missing test model at {shipped}");
        File.Copy(Path.Combine(shipped, "model.onnx"), Path.Combine(bundle, "model.onnx"));
        File.Copy(Path.Combine(shipped, "events.json"), Path.Combine(bundle, "events.json"));

        // The workspace and the install destination live under the real config directory, as they
        // do in production. The id is unique per run, so cleanup never touches a real game.
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
        // The fake recorder never brings up the libobs video pipeline, so no frame source exists;
        // register an inert one so the detector can start and hold the model reference the install
        // path has to release.
        FrameSourceRegistry.SetResolver(() => new InertFrameSource());
        try
        {
            // The recording game deliberately is not the game whose model is activated.
            Assert.True(host.StartRecording("recording-game-" + Guid.NewGuid().ToString("N")));
            host.ActivateRecordingModel(gameId);
            Assert.Equal(gameId, ActiveDetectionGameId(host));
            // The active detector holds the session, so invalidating without stopping it fails.
            // This is the state the install path must walk through.
            Assert.Throws<InvalidOperationException>(() => ModelService.InvalidateModel(gameId));

            var result = host.InstallTrainingModel(gameId, modelSource);

            Assert.True(File.Exists(result.ModelPath));
            Assert.Equal(File.ReadAllBytes(modelSource), File.ReadAllBytes(result.ModelPath));
            // Detection is back on the activated model, not on the recording game.
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
