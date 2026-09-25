// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using System.Text.Json.Nodes;
using System.ComponentModel;
using System.Diagnostics;
using Serilog;
using Serilog.Events;
using Tript.App.Content;
using Tript.App.Ipc;
using Tript.App.Models;
using Tript.Core;
using Tript.Detection;
using Tript.GameDiscovery;
using Tript.Media;
using Tript.Obs;
using Tript.Recorder;
using Tript.Settings;
#if TRIPT_TRAINING
using Tript.App.Training;
#endif
using RecorderStateMachine = Tript.Recorder.Recorder;
using SettingsModel = Tript.Settings.Settings;

namespace Tript.App;

internal sealed partial class AppHost
{
    private void WireAutoStart()
    {
        if (_options.FakeRecorder)
            return;

        if (_recorder is null)
            EnsureRecorderBuilt(SettingsResolver.Resolve(_settingsStore.Load()));

        if (_recorder is null)
            return;

        var targets = BuildDetectionTargets();

        _detector = new ProcessNameGameDetector(targets);
        _detector.GameStarted += DetectedGameStarted;
        _detector.GameStopped += DetectedGameStopped;
        _detector.Start();

        _fullscreenDetector = OperatingSystem.IsLinux()
            ? FullscreenGameDetector.ForLinux(targets, InstalledGameRoots)
            : new FullscreenGameDetector(targets);
        _fullscreenDetector.CandidateFound += OnFullscreenCandidateFound;
        _fullscreenDetector.CandidateCleared += OnFullscreenCandidateCleared;
        _fullscreenDetector.Start();
    }

    private void DetectedGameStarted(DetectedGameProcess process)
    {
        var (owner, gameId) = TrackDetectedGameStarted(process);
        PushState(IsRecording, CurrentGameId);
        EnsureManagedModel(gameId);
        if (!ShouldAutoRecord(gameId))
            return;

        RunGuarded(() => StartDetectedGameRecording(gameId, owner), "starting the recording for a detected game");
    }

    internal bool ShouldAutoRecord(string gameId) =>
        SettingsResolver.ResolveAutoRecord(_settingsStore.Load(), gameId);

    private void StartDetectedGameRecording(string gameId, string owner)
    {
        if (_disposed || _shuttingDown)
            return;

        lock (_recorderGate)
        {
            if (!_detectedGames.Contains(owner))
                return;
        }

        _ = StartRecordingInternal(gameId, owner);

        lock (_recorderGate)
        {
            if (Volatile.Read(ref _recordingProcessOwner) is null)
            {
                _detectedGames.Remove(owner);
                PushState(IsRecording, CurrentGameId);
            }
        }
    }

    internal (string Owner, string GameId) TrackDetectedGameStarted(DetectedGameProcess process)
    {
        return (_detectedGames.Add(process), process.GameId);
    }

    internal static string DetecteeOwner(DetectedGameProcess process) => DetectedGameTracker.OwnerOf(process);

    internal void DetectedGameStopped(DetectedGameProcess process)
    {
        var owner = DetecteeOwner(process);
        var replacement = _detectedGames.RemoveAndFindReplacement(process);

        if (!string.Equals(Volatile.Read(ref _recordingProcessOwner), owner, StringComparison.Ordinal))
        {
            PushState(IsRecording, CurrentGameId);
            return;
        }

        if (replacement is null)
            _captureWaitCancellation?.Cancel();

        lock (_recorderGate)
        {
            if (!string.Equals(_recordingProcessOwner, owner, StringComparison.Ordinal))
                return;

            replacement ??= _detectedGames.LatestOwner(process.GameId);
            if (replacement is not null)
            {
                Volatile.Write(ref _recordingProcessOwner, replacement);
                PushState(IsRecording, CurrentGameId);
                return;
            }

            if (!StopRecordingLocked())
                return;

            if (_detectedGames.LatestGameId() is { } nextGameId
                && _detectedGames.LatestOwner(nextGameId) is { } nextOwner)
            {
                RunGuarded(() => StartDetectedGameRecording(nextGameId, nextOwner),
                    "starting the recording for the next detected game");
            }
        }
    }

    internal string? CurrentDetectedGameId() => _detectedGames.LatestGameId();

    private string? DetectedProcessFor(string gameId) => _detectedGames.LatestOwner(gameId);

    private bool StartDetection(string gameId)
    {
        _detectionHost?.Stop();
        _detectionHost?.Dispose();
        _detectionHost = null;
        _activeDetectionGameId = null;

        var detector = new VisualEventDetectorAdapter(new VisualEventDetector());
        _detectionHost = new DetectionHost(detector, onAutomaticClipBookmark: RememberAutomaticClipBookmark);
        if (_detectionHost.Start(gameId))
        {
            _activeDetectionGameId = gameId;
            PushGameList();
            return true;
        }

        _detectionHost.Dispose();
        _detectionHost = null;
        Log.Write(ModelService.HasDetectionBundleForGame(gameId) ? LogEventLevel.Warning : LogEventLevel.Information,
            "AppHost: automatic detection did not start for {GameId}; recording continues without automatic bookmarks",
            gameId);
        return false;
    }

    private void StopDetection()
    {
        _detectionHost?.Stop();
        _detectionHost?.Dispose();
        _detectionHost = null;
        _activeDetectionGameId = null;
    }

    private void EnsureManagedModel(string gameId)
    {
        lock (_recorderGate)
        {
            if (_shuttingDown)
                return;
        }

        if (_modelManager is null || !_gameCatalog.Entries.Any(game =>
                string.Equals(game.GameId, gameId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _ = _modelManager.EnsureModelAsync(gameId);
    }

    private Task ActivateDownloadedModelAsync(string gameId, string stagedPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_recorderGate)
        {
            if (_shuttingDown)
                throw new OperationCanceledException(cancellationToken);

            var startWhenIdle = ShouldStartDetectionForLateModel(gameId,
                recording: _recorder?.Snapshot.State == RecorderState.Recording,
                stopPending: _stopFinalizationPending,
                activeDetectionGameId: _activeDetectionGameId,
                ownedByDetectedProcess: Volatile.Read(ref _recordingProcessOwner) is not null,
                currentGameId: _currentGameId,
                canonical: _gameIdAliases.Resolve);

            var startedLate = ActivateDownloadedModelCore(gameId, _activeDetectionGameId, StopDetection,
                () =>
                {
                    ModelService.InvalidateModel(gameId);
                    GameModelInstaller.InstallValidatedDirectory(gameId, stagedPath, GameModelPaths.ModelsRoot);
                }, StartDetection, startWhenIdle);

            if (startedLate)
            {
                Log.Information("AppHost: automatic detection started mid-recording for {GameId} once its model arrived",
                    gameId);
                PushState(IsRecording, CurrentGameId);
            }
        }

#if TRIPT_TRAINING
        PushAvailableRecordingModels();
#endif
        return Task.CompletedTask;
    }

    internal static bool ShouldStartDetectionForLateModel(string gameId, bool recording, bool stopPending,
        string? activeDetectionGameId, bool ownedByDetectedProcess, string? currentGameId,
        Func<string?, string?> canonical)
    {
        if (!recording || stopPending || activeDetectionGameId is not null || !ownedByDetectedProcess
            || currentGameId is null)
        {
            return false;
        }

        return string.Equals(canonical(currentGameId) ?? currentGameId, canonical(gameId) ?? gameId,
            StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ActivateDownloadedModelCore(string gameId, string? activeDetectionGameId,
        Action stopDetection, Action install, Func<string, bool> startDetection, bool startWhenIdle = false)
    {
        var restart = string.Equals(activeDetectionGameId, gameId, StringComparison.OrdinalIgnoreCase);
        if (restart)
            stopDetection();

        try
        {
            install();
        }
        finally
        {
            if (restart)
                startDetection(gameId);
        }

        return !restart && startWhenIdle && startDetection(gameId);
    }

    private void OnModelStatusChanged(IReadOnlyList<GameModelStatus> statuses) =>
        _ipc.Broadcast("modelStatus", new GameModelStatusMessage { Models = statuses });

    internal void PushModelStatus()
    {
        if (_modelManager is not null)
            OnModelStatusChanged(_modelManager.Snapshot());
    }
}
