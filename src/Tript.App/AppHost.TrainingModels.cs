// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

#if TRIPT_TRAINING

using Serilog;
using Tript.App.Training;
using Tript.Detection;

namespace Tript.App;

internal sealed partial class AppHost
{
    internal TrainingInstallResult InstallTrainingModel(string gameId, string? modelSourcePath,
        string? ocrModelSourcePath = null, string? ocrDictionarySourcePath = null,
        string? ocrDetectorSourcePath = null)
    {
        lock (_recorderGate)
        {
            var restart = ShouldRestartDetectionForModelInstall(IsRecording, _activeDetectionGameId, gameId);
            if (restart)
                StopDetection();

            try
            {
                var definitions = TrainingWorkspace.ForGame(gameId).LoadDefinitions();
                if (definitions.Any(definition => definition.DetectionKind == DetectionKind.Object)
                    && modelSourcePath is null)
                {
                    var runtimeModel = ModelService.GetModelPath(gameId);
                    if (File.Exists(runtimeModel)) modelSourcePath = runtimeModel;
                }
                if (definitions.Any(definition => definition.DetectionKind == DetectionKind.Ocr)
                    && (ocrModelSourcePath is null || ocrDictionarySourcePath is null))
                {
                    var runtimeOcrModel = ModelService.GetOcrModelPath(gameId);
                    var runtimeOcrDictionary = ModelService.GetOcrDictionaryPath(gameId);
                    var runtimeOcrDetector = ModelService.GetOcrDetectorPath(gameId);
                    if (File.Exists(runtimeOcrModel) && File.Exists(runtimeOcrDictionary))
                    {
                        ocrModelSourcePath ??= runtimeOcrModel;
                        ocrDictionarySourcePath ??= runtimeOcrDictionary;
                        if (File.Exists(runtimeOcrDetector)) ocrDetectorSourcePath ??= runtimeOcrDetector;
                    }
                }
                ModelService.InvalidateModel(gameId);
                var result = TrainingModelInstaller.Install(TrainingWorkspace.ForGame(gameId), modelSourcePath,
                    ocrModelSourcePath, ocrDictionarySourcePath, ocrDetectorSourcePath);
                if (restart)
                    StartDetection(gameId);
                PushAvailableRecordingModels();
                return result;
            }
            catch
            {
                if (restart)
                    StartDetection(gameId);
                throw;
            }
        }
    }

    internal static bool ShouldRestartDetectionForModelInstall(bool isRecording,
        string? activeDetectionGameId, string installedGameId) =>
        isRecording && string.Equals(activeDetectionGameId, installedGameId,
            StringComparison.OrdinalIgnoreCase);

    private void RemoveInstalledTrainingModel(string gameId)
    {
        lock (_recorderGate)
        {
            var restart = IsRecording
                && string.Equals(_activeDetectionGameId, gameId, StringComparison.OrdinalIgnoreCase);
            if (restart)
                StopDetection();
            try
            {
                ModelService.InvalidateModel(gameId);
                var installedRoot = TrainingWorkspace.ForGame(gameId, TrainingPaths.InstalledModelsPath).RootPath;
                if (Directory.Exists(installedRoot))
                    Directory.Delete(installedRoot, recursive: true);
                if (restart)
                    StartDetection(gameId);
                PushAvailableRecordingModels();
            }
            catch
            {
                if (restart)
                    StartDetection(gameId);
                throw;
            }
        }
    }

    internal void PushAvailableRecordingModels()
    {
        var models = ModelService.GetLoadableDetectionGameIds()
            .Select(gameId => new AvailableRecordingModel
            {
                GameId = gameId,
                Name = GameList.FirstOrDefault(game => game.Id.Equals(gameId,
                    StringComparison.OrdinalIgnoreCase))?.Name ?? gameId,
            })
            .ToList();
        _ipc.Broadcast("availableRecordingModels", new AvailableRecordingModelsMessage { Models = models });
    }

    internal void ActivateRecordingModel(string? gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            PushError("Choose a model to load.");
            return;
        }

        lock (_recorderGate)
        {
            if (!IsRecording)
            {
                PushError("Start a recording before loading a model.");
                return;
            }
            if (!ModelService.HasDetectionBundleForGame(gameId))
            {
                PushError($"No model is available for {gameId}.");
                return;
            }
            if (string.Equals(_activeDetectionGameId, gameId, StringComparison.OrdinalIgnoreCase))
            {
                AssignActiveRecordingToGame(gameId);
                PushState(true, _currentGameId);
                return;
            }

            var previousGameId = _activeDetectionGameId;

            ActivateRecordingModelCore(gameId, previousGameId, StartDetection,
                () => AssignActiveRecordingToGame(gameId),
                () => PushError($"The model for {gameId} could not be loaded."),
                () => PushState(true, _currentGameId));
        }
    }

    internal static void ActivateRecordingModelCore(string gameId, string? previousGameId,
        Func<string, bool> startDetection, Action onActivated, Action pushError, Action pushState)
    {
        var activated = false;
        try
        {
            activated = startDetection(gameId);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "AppHost: detection for {GameId} could not start.", gameId);
        }

        if (!activated)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(previousGameId))
                    startDetection(previousGameId);
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "AppHost: detection for {GameId} could not be restored.", previousGameId);
            }
            pushError();
        }
        else
        {
            onActivated();
        }
        pushState();
    }

    private void AssignActiveRecordingToGame(string gameId)
    {
        if (_pendingMetadata is null)
            return;

        var game = GameList.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, gameId, StringComparison.OrdinalIgnoreCase))?.Name ?? gameId;
        var originalGame = _pendingSessionReassignment?.OriginalGame ?? _pendingMetadata.Game;
        var originalGameId = _pendingSessionReassignment?.OriginalGameId ?? _pendingMetadata.GameId;

        if (string.Equals(originalGameId, gameId, StringComparison.OrdinalIgnoreCase))
        {
            _pendingSessionReassignment = null;
            _pendingMetadata.Game = originalGame;
            _pendingMetadata.GameId = originalGameId;
            _currentGameId = originalGameId;
            return;
        }

        _pendingSessionReassignment = new PendingSessionReassignment(
            originalGame, originalGameId, game, gameId);
        _pendingMetadata.Game = game;
        _pendingMetadata.GameId = gameId;
        _currentGameId = gameId;
    }
}

#endif
