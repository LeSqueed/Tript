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
    private enum StartRecordingResult
    {
        Started,
        AlreadyRunning,
        ShuttingDown,
        NoDetectedGame,
        UnsupportedMode,
        RecorderRefused,
    }

    internal void StartRecordingOrReport(string? gameId, string? displayId = null, bool applyDisplay = false)
    {
        var result = TryStartRecording(gameId, displayId, applyDisplay);
        if (result == StartRecordingResult.Started)
            return;

        PushError(result switch
        {
            StartRecordingResult.NoDetectedGame =>
                "Recording did not start because no game is detected. Set the capture method to Auto or Display to record the desktop.",
            StartRecordingResult.AlreadyRunning => "A recording is already running or still stopping.",
            StartRecordingResult.ShuttingDown => "Recording did not start because Tript is shutting down.",
            StartRecordingResult.UnsupportedMode =>
                "Recording did not start because the selected recording mode is not supported.",
            _ => "Recording did not start because the recorder refused to start.",
        });
    }

    internal void StopRecordingOrReport()
    {
        if (!StopRecording())
        {
            var state = _recorder?.Snapshot.State;
            PushError(state == RecorderState.Stopping
                ? "The recording is still stopping; it will remain active until the output finishes."
                : "There was no recording to stop.");
        }
    }

    internal bool StartRecording(string? gameId, string? displayId = null, bool applyDisplay = false)
        => TryStartRecording(gameId, displayId, applyDisplay) == StartRecordingResult.Started;

    private StartRecordingResult TryStartRecording(string? gameId, string? displayId = null,
        bool applyDisplay = false)
    {
        var effectiveGameId = gameId ?? CurrentDetectedGameId();
        var processOwner = effectiveGameId is null ? null : DetectedProcessFor(effectiveGameId);
        return StartRecordingInternal(effectiveGameId, processOwner, applyDisplay, displayId);
    }

    private StartRecordingResult StartRecordingInternal(string? gameId, string? processOwner = null,
        bool applyDisplay = false, string? displayId = null)
    {
        IRecorderSession? hookWaitSession = null;
        CancellationTokenSource? waitCancellation = null;
        bool hookWaitCancelled = false;
        SettingsModel settings;
        ResolvedRecorderSettings resolved;
        string sessionPath;
        string? effectiveGameId;

        lock (_recorderGate)
        {
            effectiveGameId = processOwner is null ? null : gameId;
            if (processOwner is not null && effectiveGameId is not null)
                EnsureManagedModel(effectiveGameId);

            if (_shuttingDown)
                return StartRecordingResult.ShuttingDown;

            if (_stopFinalizationPending)
                return StartRecordingResult.AlreadyRunning;

            if (_recorder is not null && _recorder.Snapshot.State != RecorderState.Idle)
                return StartRecordingResult.AlreadyRunning;

            lock (_automaticClipGate)
                _liveHighlightsEnabledAtSessionStart = false;

            settings = _settingsStore.Load();
            resolved = SettingsResolver.Resolve(settings, effectiveGameId);

            if (applyDisplay && resolved.CaptureMethod != DisplayCaptureMethod.Game)
                resolved.Display = displayId;

            if (processOwner is null && resolved.CaptureMethod == DisplayCaptureMethod.Game)
                return StartRecordingResult.NoDetectedGame;

            if (!resolved.Mode.IsAlphaSupported())
                return StartRecordingResult.UnsupportedMode;

            sessionPath = BuildSessionPath(effectiveGameId, resolved.Mode.RecordsSession());
            resolved.OutputPath = resolved.Mode.RecordsSession()
                ? sessionPath
                : BuildReplayBufferPath();

            EnsureRecorderBuilt(resolved);

            if (effectiveGameId is not null)
                RetargetGameCapture(effectiveGameId);

            Volatile.Write(ref _recordingProcessOwner, processOwner);
            SetBackgroundWorkSuspendedForRecording(true);

            if (processOwner is not null &&
                _recorderSession is { } session &&
                session.Policy.IncludesGameCapture && session.HasGameCaptureSource)
            {
                Log.Information("AppHost: waiting for the {GameId} game-capture hook before recording starts",
                    effectiveGameId);
                waitCancellation = new CancellationTokenSource();
                _captureWaitCancellation = waitCancellation;
                if (Volatile.Read(ref _recordingStopRequested) != 0)
                    waitCancellation.Cancel();
                session.PlaceSourceOnChannel();
                hookWaitSession = session;
            }
        }

        if (hookWaitSession is null)
            return FinishStartRecording(effectiveGameId, processOwner, settings, resolved, sessionPath);

        try
        {
            var hasFallback = hookWaitSession.HasDisplayFallback;
            var deadline = hasFallback ? hookWaitSession.Policy.GameCaptureTimeout : Timeout.InfiniteTimeSpan;
            var warningAfter = hasFallback ? TimeSpan.Zero : hookWaitSession.Policy.GameCaptureTimeout;
            var hookReady = hookWaitSession.WaitForGameCapture(
                deadline,
                warningAfter,
                () => PushWarning("Still connecting game capture. Recording will start when the hook is ready."),
                () => PushWarning(null),
                waitCancellation!.Token);
            hookWaitCancelled = waitCancellation.IsCancellationRequested;
            if (!hookReady && !hookWaitCancelled && hasFallback)
            {
                Log.Information("AppHost: the {GameId} game-capture hook did not attach within {Timeout}s; " +
                                "starting the recording on the display layer and keeping the hook retry",
                    effectiveGameId, deadline.TotalSeconds);
            }
        }
        finally
        {
            waitCancellation?.Dispose();
        }

        lock (_recorderGate)
        {
            try
            {
                var sessionStillOurs = !_disposed && ReferenceEquals(_recorderSession, hookWaitSession);
                if (sessionStillOurs)
                    hookWaitSession.ClearSourceFromChannel();
                _captureWaitCancellation = null;
                PushWarning(null);

                if (hookWaitCancelled)
                    return StartRecordingResult.RecorderRefused;
                if (_shuttingDown || _disposed)
                    return StartRecordingResult.ShuttingDown;
                if (sessionStillOurs && !_stopFinalizationPending &&
                    _recorder is { Snapshot: { State: RecorderState.Idle } })
                {
                    return FinishStartRecording(effectiveGameId, processOwner, settings, resolved, sessionPath);
                }

                return StartRecordingResult.AlreadyRunning;
            }
            finally
            {
                if (_recorder is null || _recorder.Snapshot.State == RecorderState.Idle)
                {
                    Interlocked.CompareExchange(ref _recordingProcessOwner, null, processOwner);
                    SetBackgroundWorkSuspendedForRecording(false);
                }
            }
        }
    }

    private StartRecordingResult FinishStartRecording(string? effectiveGameId, string? processOwner,
        SettingsModel settings, ResolvedRecorderSettings resolved, string sessionPath)
    {
        var recordingStarted = false;
        try
        {
            if (!_recorder!.Start(resolved))
                return StartRecordingResult.RecorderRefused;
            recordingStarted = true;

            _activeOutputPath = resolved.Mode.RecordsSession() ? resolved.OutputPath : null;
            _activeSessionPath = sessionPath;
            _pendingSessionReassignment = null;
            _activeRecordingMode = resolved.Mode;
            _currentGameId = effectiveGameId;
            var gameName = GameDisplayName(effectiveGameId);
            var startedUtc = DateTime.UtcNow;
            _pendingMetadata = new RecordingMetadata
            {
                Game = gameName,
                GameId = effectiveGameId,
                ContentType = ContentType.Recording,
                StartTime = startedUtc.ToLocalTime(),
            };

            _sessionTracker.Start(startedUtc);
            lock (_automaticClipGate)
            {
                _automaticClipBookmarks.Clear();
                _liveHighlightRegions.Clear();
                _liveHighlightBookmarkIds.Clear();
                _recordingStartUtc = startedUtc;
                _liveHighlightsEnabled = settings.Recording.AutomaticClipsEnabled
                    && resolved.Mode.UsesReplayBuffer();
                _liveHighlightsEnabledAtSessionStart = _liveHighlightsEnabled;
                _liveHighlightCancellation?.Dispose();
                _liveHighlightCancellation = _liveHighlightsEnabled
                    ? new CancellationTokenSource()
                    : null;
                _liveHighlightTasks.Clear();
            }

            if (processOwner is not null && effectiveGameId is not null)
                StartDetection(effectiveGameId);
            else
                StopDetection();

            PushState(recording: true, effectiveGameId);
            PushContent();
            if (resolved.Mode is RecordingMode.ReplayBufferOnly)
            {
                RequestNotification(NotificationKind.RecordingStarted, "Buffering started",
                    string.IsNullOrWhiteSpace(gameName) ? "Tript is buffering." : $"Tript is buffering {gameName}.");
            }
            else
            {
                RequestNotification(NotificationKind.RecordingStarted, "Recording started",
                    string.IsNullOrWhiteSpace(gameName) ? "Tript is recording." : $"Tript is recording {gameName}.");
            }
            return StartRecordingResult.Started;
        }
        finally
        {
            if (!recordingStarted &&
                (_recorder is null || _recorder.Snapshot.State == RecorderState.Idle))
            {
                Interlocked.CompareExchange(ref _recordingProcessOwner, null, processOwner);
                SetBackgroundWorkSuspendedForRecording(false);
            }
        }
    }

    internal bool StopRecording()
    {
        Interlocked.Exchange(ref _recordingStopRequested, 1);
        _captureWaitCancellation?.Cancel();
        lock (_recorderGate)
        {
            try
            {
                return StopRecordingLocked();
            }
            finally
            {
                Volatile.Write(ref _recordingStopRequested, 0);
            }
        }
    }

    private bool StopRecordingLocked()
    {
        if (_recorder is null || _recorder.Snapshot.State == RecorderState.Idle)
            return false;

        StopLiveAutomaticHighlights();

        if (!_recorder.Stop())
            return false;

        if (!_recorder.WaitForIdle(_recorderStopTimeout))
        {
            _stopFinalizationPending = true;
            var recorder = _recorder;
            ThreadPool.QueueUserWorkItem(_ => CompletePendingStop(recorder));
            Log.Warning("AppHost: recording output did not finish stopping within {Timeout}; leaving the recording in Stopping state until its callback arrives",
                _recorderStopTimeout);
            return false;
        }

        FinalizeStoppedRecordingLocked();
        return true;
    }

    private void CompletePendingStop(RecorderStateMachine recorder)
    {
        while (true)
        {
            lock (_recorderGate)
            {
                if (_disposed || !ReferenceEquals(_recorder, recorder))
                {
                    _stopFinalizationPending = false;
                    return;
                }

                if (recorder.Snapshot.State == RecorderState.Idle)
                {
                    _stopFinalizationPending = false;
                    FinalizeStoppedRecordingLocked();
                    return;
                }
            }

            recorder.WaitForIdle(TimeSpan.FromMilliseconds(250));
        }
    }

    private void FinalizeStoppedRecordingLocked()
    {
        _recorder!.DrainCompletedOutput();
        StopDetection();
        SetBackgroundWorkSuspendedForRecording(false);

        if (_pendingSessionReassignment is { } reassignment
            && !RelocateStoppedRecording(reassignment)
            && _pendingMetadata is not null)
        {
            _pendingMetadata.Game = reassignment.OriginalGame;
            _pendingMetadata.GameId = reassignment.OriginalGameId;
        }

        var sourcePath = _activeOutputPath;
        var recordsSession = _activeRecordingMode?.RecordsSession() == true;
        List<Bookmark> automaticBookmarks;
        HashSet<Guid> liveBookmarkIds;
        bool automaticClipsWereLive;
        lock (_automaticClipGate)
        {
            automaticBookmarks = _automaticClipBookmarks.ToList();
            _automaticClipBookmarks.Clear();
            liveBookmarkIds = _liveHighlightBookmarkIds.ToHashSet();
            _liveHighlightRegions.Clear();
            _liveHighlightBookmarkIds.Clear();
            automaticClipsWereLive = _liveHighlightsEnabledAtSessionStart;
            _liveHighlightsEnabledAtSessionStart = false;
            _liveHighlightsEnabled = false;
        }

        var session = _sessionTracker.Stop();
        if (session is not null && _pendingMetadata is not null && recordsSession)
        {
            _pendingMetadata.Bookmarks = session.Bookmarks.ToList();
            WriteMetadataRecord(_pendingMetadata);

            if (sourcePath is not null && _pendingMetadata.VideoPath.Length > 0
                && automaticClipsWereLive)
            {
                var unsavedBookmarks = automaticBookmarks
                    .Where(bookmark => !liveBookmarkIds.Contains(bookmark.Id))
                    .ToList();
                if (unsavedBookmarks.Count > 0)
                    QueueAutomaticClips(sourcePath, _pendingMetadata.VideoPath, unsavedBookmarks,
                        _pendingMetadata.GameId);
            }
        }

        _pendingMetadata = null;
        _activeOutputPath = null;
        _activeSessionPath = null;
        _pendingSessionReassignment = null;
        var stoppedMode = _activeRecordingMode;
        _activeRecordingMode = null;
        _currentGameId = null;
        Volatile.Write(ref _recordingProcessOwner, null);

        PushState(recording: false, null);
        if (stoppedMode is RecordingMode.ReplayBufferOnly)
            RequestNotification(NotificationKind.RecordingStopped, "Buffering stopped", "The replay buffer has stopped.");
        else
            RequestNotification(NotificationKind.RecordingStopped, "Recording stopped", "The recording is ready in your library.");
    }

    private bool RelocateStoppedRecording(PendingSessionReassignment reassignment)
    {
        if (_activeSessionPath is null)
            return false;

        var sourceSessionPath = _activeSessionPath;
        var targetSessionPath = Path.Combine(EffectiveRoot, GameFolderName(reassignment.TargetGameId),
            ContentLayout.Sessions, Path.GetFileName(sourceSessionPath));
        var sourceRelative = RelativeToRoot(sourceSessionPath);
        var targetRelative = RelativeToRoot(targetSessionPath);
        var linkedHighlights = _clipTitles.EnumerateRecords()
            .Where(entry => entry.Record.IsAutomatic
                && !string.IsNullOrWhiteSpace(entry.Record.SourceSessionPath)
                && string.Equals(NormalizeSourcePath(entry.Record.SourceSessionPath), sourceRelative,
                    ContentPathComparison))
            .ToList();
        var sourceHighlightsDirectory = HighlightsDirectoryPathForSource(sourceSessionPath);
        var targetHighlightsDirectory = HighlightsDirectoryPathForSource(targetSessionPath);
        var moves = new List<(string Source, string Destination)>();

        if (_activeOutputPath is not null && File.Exists(_activeOutputPath)
            && !string.Equals(Path.GetFullPath(_activeOutputPath), Path.GetFullPath(targetSessionPath),
                ContentPathComparison))
        {
            moves.Add((_activeOutputPath, targetSessionPath));
        }

        foreach (var (clipFileName, _) in linkedHighlights)
        {
            var source = Path.Combine(sourceHighlightsDirectory, clipFileName);
            var destination = Path.Combine(targetHighlightsDirectory, clipFileName);
            if (File.Exists(source)
                && !string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination),
                    ContentPathComparison))
            {
                moves.Add((source, destination));
            }
        }

        var collision = moves.FirstOrDefault(move => File.Exists(move.Destination));
        if (collision != default)
        {
            PushError($"The recording could not be moved to {reassignment.TargetGame} because '{Path.GetFileName(collision.Destination)}' already exists there.");
            return false;
        }

        var completedMoves = new List<(string Source, string Destination)>();
        var updatedRecords = new List<(string ClipFileName, ClipTitleRecord Record)>();
        try
        {
            foreach (var move in moves)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(move.Destination)!);
                File.Move(move.Source, move.Destination);
                completedMoves.Add(move);
            }

            foreach (var (clipFileName, record) in linkedHighlights)
            {
                if (!_clipTitles.SaveAutomaticAssignment(clipFileName, targetRelative,
                        reassignment.TargetGame, reassignment.TargetGameId))
                {
                    throw new IOException($"The metadata for '{clipFileName}' could not be updated.");
                }
                updatedRecords.Add((clipFileName, record));
            }

            _activeSessionPath = targetSessionPath;
            if (_activeOutputPath is not null)
                _activeOutputPath = targetSessionPath;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            foreach (var (clipFileName, record) in updatedRecords)
            {
                _clipTitles.SaveAutomaticAssignment(clipFileName, sourceRelative,
                    record.Game, record.GameId);
            }
            for (var index = completedMoves.Count - 1; index >= 0; index--)
            {
                var move = completedMoves[index];
                try
                {
                    if (File.Exists(move.Destination) && !File.Exists(move.Source))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(move.Source)!);
                        File.Move(move.Destination, move.Source);
                    }
                }
                catch (Exception rollbackException) when (rollbackException is IOException or UnauthorizedAccessException)
                {
                    Log.Error(rollbackException, "AppHost: recording reassignment rollback failed for {Path}", move.Source);
                }
            }

            Log.Warning(exception, "AppHost: recording could not be reassigned to {GameId}",
                reassignment.TargetGameId);
            PushError($"The recording could not be moved to {reassignment.TargetGame}, so it remains in its original game.");
            return false;
        }
    }

    private static void DisposeRecorderResources(RecorderStateMachine? recorder,
        IRecorderSession? recorderSession, ObsSource? colourSource, ObsRuntime? runtime)
    {
        try { recorder?.Dispose(); }
        catch (Exception exception) { Log.Warning(exception, "AppHost: deferred recorder disposal failed"); }

        try { recorderSession?.Dispose(); }
        catch (Exception exception) { Log.Warning(exception, "AppHost: deferred recorder session disposal failed"); }

        try { colourSource?.Dispose(); }
        catch (Exception exception) { Log.Warning(exception, "AppHost: deferred colour source disposal failed"); }

        try { runtime?.Dispose(); }
        catch (Exception exception) { Log.Warning(exception, "AppHost: deferred OBS runtime disposal failed"); }
    }

    private void WriteMetadataRecord(RecordingMetadata metadata)
    {
        if (_activeOutputPath is null || !File.Exists(_activeOutputPath))
            return;

        metadata.VideoPath = RelativeToRoot(_activeOutputPath);

        lock (_metadata.WriteGate)
        {
            if (metadata.DurationSeconds is null)
            {
                var existing = _metadata.Read(Path.GetFileName(_activeOutputPath));
                if (existing.State == StoredRecordState.Loaded)
                    metadata.DurationSeconds = existing.Record!.DurationSeconds;
            }

            _metadata.Save(metadata);
        }
        PushContent();
    }

    private void EnsureRecorderBuilt(ResolvedRecorderSettings settings)
    {
        if (_options.FakeRecorder)
        {
            if (_recorder is not null)
                return;

            _recorderSession = new FakeRecorderSession();
            _recorder = new RecorderStateMachine(_recorderSession, settings);
            return;
        }

        var policy = CapturePolicy.From(settings);
        if (_recorder is not null)
        {
            if (_recorderSession is not ObsRecorderSession existing || existing.Policy == policy)
                return;

            Log.Information("AppHost: the capture policy changed to {Method}; rebuilding the recording scene.",
                policy.Method);
            _recorder.Dispose();
            _recorder = null;
            _recorderSession.Dispose();
            _recorderSession = null;
        }

        if (_runtime is null)
            throw new InvalidOperationException("The real recorder needs a libobs runtime; none was started.");

        if (_colourSource is null)
        {
            using var colourSettings = new ObsSettings();
            colourSettings.SetInt("color", unchecked((int)0xFF000000));
            _colourSource = ObsSource.CreatePrivate("color_source", "app colour", colourSettings);
        }

        var session = new ObsRecorderSession(_runtime, _colourSource, gameCaptureTarget: null, policy);
        _recorderSession = session;
        _recorder = new RecorderStateMachine(session, settings);
    }

    private void RetargetGameCapture(string gameId)
    {
        if (_recorderSession is not ObsRecorderSession session)
            return;

        var name = GameCaptureName(gameId);
        var executable = OperatingSystem.IsWindows() ? $"{name}.exe" : name;
        session.RetargetGame(new ObsGameCaptureTarget(null, null, executable));
    }

    internal string GameCaptureName(string gameId)
    {
        var entry = FindGame(gameId);
        return ExecutableNames.Normalize(entry is null ? gameId : ExecutableOf(entry));
    }

    private string BuildSessionPath(string? gameId, bool createDirectory = true)
    {
        var directory = string.IsNullOrWhiteSpace(gameId)
            ? Path.Combine(EffectiveRoot, ContentLayout.Sessions)
            : Path.Combine(EffectiveRoot, GameFolderName(gameId), ContentLayout.Sessions);
        if (createDirectory)
            Directory.CreateDirectory(directory);

        var name = $"session-{DateTime.Now:yyyyMMdd-HHmmssfff}.mp4";
        return Path.Combine(directory, name);
    }

    private static string BuildReplayBufferPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Tript", "replay");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "replay-buffer.mp4");
    }

    private string ClipDirectoryForSource(string sourcePath) =>
        ContentLayout.SiblingOfSessions(EffectiveRoot, sourcePath, ContentLayout.Clips);

    private string HighlightsDirectoryForSource(string sourcePath)
    {
        var directory = HighlightsDirectoryPathForSource(sourcePath);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private string HighlightsDirectoryPathForSource(string sourcePath) =>
        ContentLayout.SiblingOfSessions(EffectiveRoot, sourcePath, ContentLayout.Highlights);

    private string GameFolderName(string gameId)
    {
        var game = FindGame(gameId)?.Name;
        return SafeDirectoryName(string.IsNullOrWhiteSpace(game) ? gameId : game, OperatingSystem.IsWindows());
    }

    private const string UnknownGameFolder = "Unknown Game";

    private static readonly char[] WindowsInvalidFileNameChars =
        [.. "<>:\"/\\|?*", .. Enumerable.Range(0, 32).Select(code => (char)code)];

    internal static string SafeDirectoryName(string value, bool windowsRules)
    {
        var invalid = windowsRules ? WindowsInvalidFileNameChars : Path.GetInvalidFileNameChars();
        var name = new string(value.Trim().Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        if (windowsRules)
            name = name.TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
            return UnknownGameFolder;
        return windowsRules && IsReservedDeviceName(name) ? "_" + name : name;
    }

    private static bool IsReservedDeviceName(string name)
    {
        var stem = name.Split('.', 2)[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL"
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal)
                || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9');
    }
}
