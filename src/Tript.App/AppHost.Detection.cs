// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using System.Text.Json.Nodes;
using System.ComponentModel;
using System.Diagnostics;
using Serilog;
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
    // ---- detection ----

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

        _fullscreenDetector = new FullscreenGameDetector(targets);
        _fullscreenDetector.CandidateFound += OnFullscreenCandidateFound;
        _fullscreenDetector.CandidateCleared += OnFullscreenCandidateCleared;
        _fullscreenDetector.Start();

    }

    private void StartDiscoveryScan()
    {
        if (_detector is not null && _discovery is not null && _discoveryTask.IsCompleted
            && !_discoveryCancellation.IsCancellationRequested)
            _discoveryTask = Task.Run(() => ScanDiscoveryAsync(_discoveryCancellation.Token));
    }

    private List<GameDetectionTarget> BuildDetectionTargets()
    {
        var targets = new List<GameDetectionTarget>();
        foreach (var game in GameList)
        {
            if (string.IsNullOrWhiteSpace(game.Executable))
                continue;

            if (!game.BuiltIn)
            {
                var customPath = NormalizePickedExecutable(game.ExecutablePath);
                if (customPath is not null)
                    targets.Add(new GameDetectionTarget(game.Id, game.Executable,
                        ProcessNameGameDetector.NormalizePath(customPath)));
                continue;
            }

            var discoveredPath = DiscoveredProcessPath(game.Id, game.Executable);
            targets.Add(discoveredPath is null
                ? new GameDetectionTarget(game.Id, game.Executable)
                : new GameDetectionTarget(game.Id, game.Executable, discoveredPath));
        }

        return targets;
    }

    private string? DiscoveredProcessPath(string gameId, string executable)
    {
        GameInventory inventory;
        lock (_inventoryGate)
            inventory = _inventory;
        if (inventory.Games.IsDefaultOrEmpty)
            return null;

        var entry = _gameCatalog.EntryById(gameId);
        var normalizedExecutable = ExecutableNames.Normalize(executable);

        foreach (var installed in inventory.Games)
        {
            if (entry is not null && entry.HasStoreProduct(installed.Store, installed.ProductId.Value))
            {
                return installed.TryResolveCatalogueExecutable(
                    _discoveryFileSystem, entry.Executable, out var resolved)
                    ? ProcessNameGameDetector.NormalizePath(resolved)
                    : null;
            }

            if (entry is not null && (entry.StoreProducts is null || entry.StoreProducts.Count == 0))
            {
                if (installed.TryResolveCatalogueExecutable(
                    _discoveryFileSystem, entry.Executable, out var resolved))
                {
                    return ProcessNameGameDetector.NormalizePath(resolved);
                }

                if (MatchingExecutablePath(installed, normalizedExecutable) is { } matched)
                    return matched;
            }
        }

        return null;
    }

    private static string? MatchingExecutablePath(InstalledGame installed, string normalizedExecutable)
    {
        foreach (var path in installed.ExecutablePaths)
        {
            if (path.Length > 0 && ExecutableNames.Comparer.Equals(ExecutableNames.Normalize(path), normalizedExecutable))
                return ProcessNameGameDetector.NormalizePath(path);
        }

        return null;
    }

    private async Task ScanDiscoveryAsync(CancellationToken cancellationToken)
    {
        if (_discovery is null)
            return;

        try
        {
            await _discoveryScanSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            try
            {
                var inventory = await _discovery.DiscoverAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                lock (_inventoryGate)
                    _inventory = inventory;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                Log.Warning(exception,
                    "AppHost: launcher game discovery failed; detection continues on the packaged catalogue and custom games.");
                return;
            }

            if (_disposed || cancellationToken.IsCancellationRequested)
                return;

            var previousPaths = GameList.Select(game => (game.Id, game.ExecutablePath)).ToList();
            ReloadGameList();
            var discoveredPaths = GameList.Select(game => (game.Id, game.ExecutablePath)).ToList();
            if (!previousPaths.SequenceEqual(discoveredPaths))
            {
                RebuildDetectionTargets();
                PushGameList();
            }
        }
        finally
        {
            _discoveryScanSemaphore.Release();
        }
    }

    private void RebuildDetectionTargets()
    {
        var targets = BuildDetectionTargets();
        _detector?.UpdateTargets(targets);
        _fullscreenDetector?.UpdateKnownTargets(targets);
    }

    private void OnFullscreenCandidateFound(FullscreenGameCandidate candidate)
    {
        var normalized = ProcessNameGameDetector.NormalizePath(candidate.ExecutablePath);
        if (normalized is null)
            return;

        lock (_ignoredCandidateGate)
        {
            if (_ignoredCandidatePaths.Contains(normalized))
                return;
        }

        _ipc.Broadcast("gameCandidate", JsonSerializer.SerializeToElement(new
        {
            pid = candidate.ProcessId,
            executable = candidate.Executable,
            executablePath = normalized,
        }, Wire.Options));
    }

    private void OnFullscreenCandidateCleared(FullscreenGameCandidate candidate)
    {
        var normalized = ProcessNameGameDetector.NormalizePath(candidate.ExecutablePath);
        if (normalized is null)
            return;

        _ipc.Broadcast("gameCandidateCleared", JsonSerializer.SerializeToElement(new
        {
            executablePath = normalized,
        }, Wire.Options));
    }

    internal void IgnoreGameCandidate(string? executablePath, string? requestId = null)
    {
        var normalized = ProcessNameGameDetector.NormalizePath(executablePath);
        if (normalized is null)
        {
            PushGameCandidateActionResult(requestId, executablePath ?? string.Empty, "ignore", false,
                "The executable path is invalid.");
            return;
        }

        lock (_ignoredCandidateGate)
            _ignoredCandidatePaths.Add(normalized);
        PushGameCandidateActionResult(requestId, normalized, "ignore", true, null);
    }

    internal void AddGameCandidate(string? name, string? executablePath, string? requestId = null)
    {
        lock (_settingsUpdateGate)
            AddGameCandidateLocked(name, executablePath, requestId);
    }

    private void AddGameCandidateLocked(string? name, string? executablePath, string? requestId)
    {
        var normalized = NormalizePickedExecutable(executablePath);
        if (normalized is null)
        {
            const string error = "That executable no longer exists; select it again before adding the game.";
            PushError(error);
            PushGameCandidateActionResult(requestId, executablePath ?? string.Empty, "add", false, error);
            return;
        }

        var displayName = string.IsNullOrWhiteSpace(name)
            ? Path.GetFileNameWithoutExtension(normalized)
            : name.Trim();
        var saved = _settingsStore.TryUpdate(settings =>
        {
            settings.Game.GameList.Add(new GameSetting
            {
                Id = $"custom-{Guid.NewGuid():N}",
                Name = displayName,
                ExecutablePath = normalized,
                Integrations = new GameIntegrationSettings { Enabled = false },
            });
            return ValidateGameList(settings.Game.GameList, out var validationError)
                ? null
                : validationError;
        }, out _, out var failure);

        if (!saved)
        {
            var error = $"That game was not added: {failure ?? "the settings file could not be written."}";
            PushError(error);
            PushSettings();
            PushGameCandidateActionResult(requestId, normalized, "add", false, error);
            return;
        }

        ReloadGameList();
        RebuildDetectionTargets();
        PushGameList();
        PushSettings();
        lock (_ignoredCandidateGate)
            _ignoredCandidatePaths.Add(normalized);
        PushGameCandidateActionResult(requestId, normalized, "add", true, null);
    }

    private void PushGameCandidateActionResult(string? requestId, string executablePath, string action,
        bool success, string? error)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            return;

        _ipc.Broadcast("gameCandidateActionResult", JsonSerializer.SerializeToElement(new
        {
            requestId,
            executablePath,
            action,
            success,
            error,
        }, Wire.Options));
    }

    private void DetectedGameStarted(DetectedGameProcess process)
    {
        var (owner, gameId) = TrackDetectedGameStarted(process);
        PushState(IsRecording, CurrentGameId);
        EnsureManagedModel(gameId);
        // The auto-start can block indefinitely while the game-capture hook is awaited, and the
        // detector reports every lifecycle over one serialized callback thread. Starting the
        // recording on that thread would stall it, so the GameStopped that clears this process when
        // it exits could never be processed and the detected badge would stay up after the game
        // closed. The start runs on the thread pool; the recorder gate still serializes it against
        // every other start/stop.
        ThreadPool.QueueUserWorkItem(_ => StartDetectedGameRecording(gameId, owner));
    }

    private void StartDetectedGameRecording(string gameId, string owner)
    {
        if (_disposed || _shuttingDown)
            return;

        lock (_recorderGate)
        {
            if (!_detectedGames.Contains(owner))
                return;

            StartRecordingLocked(gameId, owner);
            // The start clears the process owner when it does not become a recording. A process that
            // is still running never fires GameStopped, so without this the detected badge would
            // linger for the life of the process even though nothing recorded.
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
                ThreadPool.QueueUserWorkItem(_ => StartDetectedGameRecording(nextGameId, nextOwner));
            }
        }
    }

    internal string? CurrentDetectedGameId() => _detectedGames.LatestGameId();

    private string? DetectedProcessFor(string gameId) => _detectedGames.LatestOwner(gameId);

    // What the catalogue says this game runs as. Null Executable means the entry predates the field,
    // where the display name was also the process name.
    private static string ExecutableOf(GameInfo game) => game.Executable ?? game.Name;

    // The detector reports a normalized process name; every path downstream of StartRecording keys off
    // a catalogue Id (per-game settings, the display name in the metadata record, the detection model).
    // Translating here is what keeps those lookups working once an executable is not also the Id — the
    // `?? gameId` fallbacks below only ever agreed with the process name by coincidence. An unmatched
    // name is passed through unchanged, which is what a manual StartRecording for an unlisted game does.
    internal string ResolveDetectedGameId(string processName)
    {
        foreach (var game in GameList)
        {
            if (game.Id.Length == 0)
                continue;

            if (ExecutableNames.Comparer.Equals(ExecutableNames.Normalize(ExecutableOf(game)), processName))
                return game.Id;
        }

        return processName;
    }

    // Metadata written before stable GameId existed stored the display name only. Recover the
    // catalogue identity when possible so old recordings participate in per-game training and clips
    // inherit the same identity as new recordings.
    private string? ResolveLegacyGameId(string? gameName)
    {
        if (string.IsNullOrWhiteSpace(gameName))
            return null;

        var exact = GameList.FirstOrDefault(game =>
            string.Equals(game.Id, gameName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(game.Name, gameName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact.Id;

        return GameList.FirstOrDefault(game => ExecutableNames.Equal(ExecutableOf(game), gameName))?.Id;
    }

    // The game a clip cut from this session belongs to. The session's on-disk metadata record is
    // authoritative; for the session being recorded right now the in-memory pending record is used,
    // because the on-disk record is only written when the recording stops.
    private (string? Game, string? GameId) ResolveGameForSession(string sourceSessionPath)
    {
        var sessionFile = Path.GetFileName(sourceSessionPath);
        if (!string.IsNullOrWhiteSpace(sessionFile))
        {
            var metadata = _metadata.Load(sessionFile);
            if (metadata is not null)
            {
                var game = string.IsNullOrWhiteSpace(metadata.Game) ? null : metadata.Game;
                var gameId = string.IsNullOrWhiteSpace(metadata.GameId)
                    ? ResolveLegacyGameId(game)
                    : metadata.GameId;
                if (game is not null || gameId is not null)
                    return (game, gameId);
            }
        }

        if (!string.IsNullOrWhiteSpace(_activeOutputPath)
            && string.Equals(Path.GetFileName(_activeOutputPath), sessionFile,
                StringComparison.OrdinalIgnoreCase)
            && _pendingMetadata is not null)
        {
            var pendingGame = string.IsNullOrWhiteSpace(_pendingMetadata.Game) ? null : _pendingMetadata.Game;
            var pendingGameId = string.IsNullOrWhiteSpace(_pendingMetadata.GameId)
                ? ResolveLegacyGameId(pendingGame)
                : _pendingMetadata.GameId;
            if (pendingGame is not null || pendingGameId is not null)
                return (pendingGame, pendingGameId);
        }

        return (null, null);
    }

    // Persists the game attribution on freshly created clips so the tag survives the source session
    // being deleted later. The on-disk session metadata is usually already present (highlights from a
    // completed recording); the pending record covers the session that is being recorded live.
    private void AttachGameToClips(IEnumerable<string> clipFiles, string sourceSessionPath)
    {
        var (game, gameId) = ResolveGameForSession(sourceSessionPath);
        if (game is null && gameId is null)
            return;

        foreach (var clipFile in clipFiles)
            _clipTitles.SaveGame(Path.GetFileName(clipFile), game, gameId);
    }

    private void StartDetection(string gameId)
    {
        _detectionHost?.Stop();
        _detectionHost?.Dispose();
        _detectionHost = null;

        var detector = new VisualEventDetectorAdapter(new VisualEventDetector());
        _detectionHost = new DetectionHost(detector, onAutomaticClipBookmark: RememberAutomaticClipBookmark);
        if (_detectionHost.Start(gameId))
        {
            PushGameList();
            return;
        }

        _detectionHost.Dispose();
        _detectionHost = null;
        Log.Warning("AppHost: automatic detection did not start for {GameId}; recording continues without automatic bookmarks",
            gameId);
    }

    private void StopDetection()
    {
        _detectionHost?.Stop();
        _detectionHost?.Dispose();
        _detectionHost = null;
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

            var restart = IsRecording && string.Equals(_currentGameId, gameId,
                StringComparison.OrdinalIgnoreCase);
            if (restart)
                StopDetection();

            try
            {
                ModelService.InvalidateModel(gameId);
                GameModelInstaller.InstallValidatedDirectory(gameId, stagedPath, GameModelPaths.ModelsRoot);
                if (restart)
                    StartDetection(gameId);
            }
            catch
            {
                if (restart)
                    StartDetection(gameId);
                throw;
            }
        }

        return Task.CompletedTask;
    }

    private void OnModelStatusChanged(IReadOnlyList<GameModelStatus> statuses) =>
        _ipc.Broadcast("modelStatus", new GameModelStatusMessage { Models = statuses });

    internal void PushModelStatus()
    {
        if (_modelManager is not null)
            OnModelStatusChanged(_modelManager.Snapshot());
    }

    private void RememberAutomaticClipBookmark(Bookmark bookmark)
    {
        var (before, after) = SettingsResolver.ResolveAutomaticClipWindow(_settingsStore.Load(), _currentGameId);
        bookmark.IsAutomaticClipCandidate = true;
        LiveHighlightRegion? regionToSchedule = null;
        CancellationToken token = default;
        lock (_automaticClipGate)
        {
            _automaticClipBookmarks.Add(bookmark);

            if (!_liveHighlightsEnabled || _liveHighlightCancellation is null)
                return;

            var start = bookmark.Time > before
                ? bookmark.Time - before
                : TimeSpan.Zero;
            var existing = _liveHighlightRegions.FirstOrDefault(region =>
                !region.SaveRequested && bookmark.Time - before <= region.End);
            if (existing is not null)
            {
                existing.End = existing.End > bookmark.Time + after
                    ? existing.End
                    : bookmark.Time + after;
                existing.BookmarkIds.Add(bookmark.Id);
                return;
            }

            regionToSchedule = new LiveHighlightRegion
            {
                Start = start,
                End = bookmark.Time + after,
            };
            regionToSchedule.BookmarkIds.Add(bookmark.Id);
            _liveHighlightRegions.Add(regionToSchedule);
            token = _liveHighlightCancellation.Token;
        }

        var task = SaveLiveAutomaticHighlightWhenReady(regionToSchedule, token);
        lock (_automaticClipGate)
            _liveHighlightTasks.Add(task);
    }

    private async Task SaveLiveAutomaticHighlightWhenReady(LiveHighlightRegion region,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var (before, _) = SettingsResolver.ResolveAutomaticClipWindow(_settingsStore.Load(), _currentGameId);
                TimeSpan delay;
                lock (_automaticClipGate)
                {
                    if (region.SaveRequested || !_liveHighlightsEnabled)
                        return;
                    delay = _recordingStartUtc + region.End + before + LiveHighlightBoundaryGrace - DateTime.UtcNow;
                }

                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

                lock (_automaticClipGate)
                {
                    if (region.SaveRequested || !_liveHighlightsEnabled)
                        return;
                    if (_recordingStartUtc + region.End + before + LiveHighlightBoundaryGrace > DateTime.UtcNow)
                        continue;
                    region.SaveRequested = true;
                }

                var recorder = _recorder;
                var sourcePath = _activeOutputPath;
                if (recorder is null || sourcePath is null)
                    return;

                var sourceSessionPath = Path.GetRelativePath(EffectiveRoot, sourcePath)
                    .Replace(Path.DirectorySeparatorChar, '/');
                var replayDirectory = Path.Combine(Path.GetTempPath(), "Tript", "replay");
                Directory.CreateDirectory(replayDirectory);
                var saveElapsed = (DateTime.UtcNow - _recordingStartUtc).TotalSeconds;
                var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var accepted = recorder.SaveReplayBuffer(replayDirectory,
                    "tript-replay-%CCYY-%MM-%DD-%hh-%mm-%ss",
                    replayPath =>
                    {
                        _ = Task.Run(() =>
                        {
                            try
                            {
                                CreateLiveAutomaticHighlight(region, sourceSessionPath, sourcePath,
                                    replayPath, saveElapsed);
                            }
                            catch (Exception exception)
                            {
                                Log.Error(exception, "AppHost: live automatic highlight failed for {SourcePath}",
                                    sourcePath);
                            }
                            finally
                            {
                                completed.TrySetResult();
                            }
                        });
                    });

                if (!accepted)
                {
                    lock (_automaticClipGate)
                        region.SaveRequested = false;
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                await completed.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // A stop gives unsaved regions one final chance to come from the active replay buffer;
            // anything that still fails is handled by the finished-session fallback.
        }
        catch (TimeoutException)
        {
            lock (_automaticClipGate)
            {
                region.Abandoned = true;
                region.SaveRequested = false;
            }
            Log.Warning("AppHost: replay buffer save did not complete for live automatic highlight");
        }
        catch (Exception exception)
        {
            Log.Error(exception, "AppHost: live automatic highlight scheduling failed");
        }
    }

    private void CreateLiveAutomaticHighlight(LiveHighlightRegion region, string sourceSessionPath,
        string sourcePath, string replayPath, double replayEndSeconds, bool deleteReplay = true)
    {
        try
        {
            if (!File.Exists(replayPath) || IsAbandonedLiveRegion(region))
                return;

            var bufferSeconds = Math.Max(1,
                _settingsStore.Load().Buffer.Duration.TotalSeconds);
            var replayStartSeconds = Math.Max(0, replayEndSeconds - bufferSeconds);
            var localStart = Math.Max(0, region.Start.TotalSeconds - replayStartSeconds);
            var localEnd = region.End.TotalSeconds - replayStartSeconds;
            if (localEnd <= localStart)
                return;

            _clipEngine ??= BuildClipEngine();
            var outputDirectory = HighlightsDirectoryForSource(sourcePath);
            var outputPath = Path.Combine(outputDirectory,
                $"{Path.GetFileNameWithoutExtension(sourcePath)}-highlight-live-{Guid.NewGuid():N}.mp4");
            var localRegion = new ClipRegion(TimeSpan.FromSeconds(localStart),
                TimeSpan.FromSeconds(localEnd));
            var results = _clipEngine.CreateClips(new ClipRequest
            {
                OperationId = $"automatic-live-{Guid.NewGuid():N}",
                SourcePath = replayPath,
                SourceSessionPath = sourceSessionPath,
                Regions = [localRegion],
                Mode = ClipMode.Combine,
                OutputPath = outputPath,
                EncoderFamily = "libx264",
                PreferStreamCopy = true,
            });

            if (IsAbandonedLiveRegion(region))
            {
                foreach (var result in results)
                {
                    try { File.Delete(result); }
                    catch (IOException exception) { Log.Warning(exception, "AppHost: abandoned live highlight could not be removed"); }
                    catch (UnauthorizedAccessException exception) { Log.Warning(exception, "AppHost: abandoned live highlight could not be removed"); }
                }
                return;
            }

            var metadataSaved = true;
            foreach (var result in results)
            {
                metadataSaved &= _clipTitles.SaveAutomatic(Path.GetFileName(result), sourceSessionPath,
                    region.Start.TotalSeconds, region.End.TotalSeconds);
            }
            AttachGameToClips(results, sourceSessionPath);
            if (!metadataSaved)
            {
                foreach (var result in results)
                {
                    try { File.Delete(result); }
                    catch (IOException exception) { Log.Warning(exception, "AppHost: live highlight could not be removed after metadata failure"); }
                    catch (UnauthorizedAccessException exception) { Log.Warning(exception, "AppHost: live highlight could not be removed after metadata failure"); }
                }
                return;
            }

            lock (_automaticClipGate)
            {
                foreach (var bookmarkId in region.BookmarkIds)
                    _liveHighlightBookmarkIds.Add(bookmarkId);
            }
            PushContent();
        }
        finally
        {
            if (deleteReplay)
            {
                try { File.Delete(replayPath); }
                catch (IOException exception) { Log.Warning(exception, "AppHost: replay temporary file could not be removed"); }
                catch (UnauthorizedAccessException exception) { Log.Warning(exception, "AppHost: replay temporary file could not be removed"); }
            }
        }
    }

    private bool IsAbandonedLiveRegion(LiveHighlightRegion region)
    {
        lock (_automaticClipGate)
            return region.Abandoned;
    }

    private void StopLiveAutomaticHighlights()
    {
        Task[] tasks;
        CancellationTokenSource? cancellation;
        lock (_automaticClipGate)
        {
            _liveHighlightsEnabled = false;
            cancellation = _liveHighlightCancellation;
            cancellation?.Cancel();
            _liveHighlightCancellation = null;
            tasks = _liveHighlightTasks.ToArray();
        }

        try
        {
            Task.WaitAll(tasks, TimeSpan.FromSeconds(2));
        }
        catch (Exception exception) when (exception is AggregateException or ObjectDisposedException)
        {
            Log.Debug(exception, "AppHost: live automatic highlight tasks did not all settle before stop");
        }
        finally
        {
            lock (_automaticClipGate)
            {
                foreach (var region in _liveHighlightRegions)
                {
                    if (region.SaveRequested && !_liveHighlightBookmarkIds.Overlaps(region.BookmarkIds))
                    {
                        region.Abandoned = true;
                        region.SaveRequested = false;
                    }
                }
            }
            cancellation?.Dispose();
        }

        SavePendingLiveHighlightsAtStop();
    }

    private void SavePendingLiveHighlightsAtStop()
    {
        var recorder = _recorder;
        var sourcePath = _activeOutputPath;
        if (recorder is null || sourcePath is null)
            return;

        List<LiveHighlightRegion> pending;
        lock (_automaticClipGate)
        {
            pending = _liveHighlightRegions
                .Where(region => !region.Abandoned
                    && !region.SaveRequested
                    && !_liveHighlightBookmarkIds.Overlaps(region.BookmarkIds))
                .ToList();
            foreach (var region in pending)
                region.SaveRequested = true;
        }

        if (pending.Count == 0)
            return;

        var sourceSessionPath = Path.GetRelativePath(EffectiveRoot, sourcePath)
            .Replace(Path.DirectorySeparatorChar, '/');
        var replayDirectory = Path.Combine(Path.GetTempPath(), "Tript", "replay");
        Directory.CreateDirectory(replayDirectory);
        var saveElapsed = (DateTime.UtcNow - _recordingStartUtc).TotalSeconds;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = recorder.SaveReplayBuffer(replayDirectory,
            "tript-replay-%CCYY-%MM-%DD-%hh-%mm-%ss",
            replayPath =>
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        foreach (var region in pending)
                            CreateLiveAutomaticHighlight(region, sourceSessionPath, sourcePath,
                                replayPath, saveElapsed, deleteReplay: false);
                    }
                    catch (Exception exception)
                    {
                        Log.Error(exception, "AppHost: pending live automatic highlights failed at stop");
                    }
                    finally
                    {
                        try { File.Delete(replayPath); }
                        catch (IOException exception) { Log.Warning(exception, "AppHost: stop replay temporary file could not be removed"); }
                        catch (UnauthorizedAccessException exception) { Log.Warning(exception, "AppHost: stop replay temporary file could not be removed"); }
                        completed.TrySetResult();
                    }
                });
            });

        if (!accepted)
        {
            lock (_automaticClipGate)
            {
                foreach (var region in pending)
                    region.SaveRequested = false;
            }
            return;
        }

        try
        {
            if (!completed.Task.Wait(TimeSpan.FromSeconds(5)))
            {
                lock (_automaticClipGate)
                {
                    foreach (var region in pending)
                    {
                        region.Abandoned = true;
                        region.SaveRequested = false;
                    }
                }
                Log.Warning("AppHost: pending live automatic highlights timed out at stop");
            }
        }
        catch (AggregateException exception)
        {
            Log.Warning(exception, "AppHost: pending live automatic highlights did not settle at stop");
        }
    }
}
