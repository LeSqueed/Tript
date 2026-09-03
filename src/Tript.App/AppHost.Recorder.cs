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
    // ---- recorder wiring ----

    // The dispatch entry points. StartRecording/StopRecording answer bool and every refusal returns
    // before the state push, so a caller that drops the bool leaves the UI showing nothing happened.
    internal void StartRecordingOrReport(string? gameId)
    {
        if (!StartRecording(gameId))
            PushError("Recording did not start. Either one is already running, or this machine refused it.");
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

    internal bool StartRecording(string? gameId)
    {
        lock (_recorderGate)
        {
            var effectiveGameId = gameId ?? CurrentDetectedGameId() ?? StartupGameId;
            return StartRecordingLocked(effectiveGameId, DetectedProcessFor(effectiveGameId));
        }
    }

    private bool StartRecordingLocked(string? gameId, string? processOwner = null)
    {
        var effectiveGameId = gameId ?? StartupGameId;
        EnsureManagedModel(effectiveGameId);

        // Detector teardown owns the callback barrier; do not admit new recording starts while it is
        // being dismantled.
        if (_shuttingDown)
            return false;

        if (_stopFinalizationPending)
            return false;

        if (_recorder is not null && _recorder.Snapshot.State != RecorderState.Idle)
            return false;

        var settings = _settingsStore.Load();
        var resolved = SettingsResolver.Resolve(settings, effectiveGameId);

        if (!resolved.Mode.IsAlphaSupported())
            return false;

        resolved.OutputPath = BuildOutputPath(settings, effectiveGameId);

        EnsureRecorderBuilt(resolved);

        // Point the session's game-capture source at the detected game before the recording starts, so
        // the recording shows the game rather than the background. win-capture keeps retrying the hook
        // while the source is shown, so a game that appears mid-recording is still picked up.
        RetargetGameCapture(effectiveGameId);

        var recordingStarted = false;
        Volatile.Write(ref _recordingProcessOwner, processOwner);
        SetBackgroundWorkSuspendedForRecording(true);
        try
        {
            if (_recorderSession is ObsRecorderSession capture &&
                capture.Policy.IncludesGameCapture && capture.HasGameCaptureSource)
            {
                Log.Information("AppHost: waiting for the {GameId} game-capture hook before recording starts",
                    effectiveGameId);
                // StopRecording publishes its intent before taking the recorder gate. Check again after
                // publishing the source so a stop that arrived during setup cannot miss this wait.
                var waitCancellation = new CancellationTokenSource();
                _captureWaitCancellation = waitCancellation;
                if (Volatile.Read(ref _recordingStopRequested) != 0)
                    waitCancellation.Cancel();
                capture.PlaceSourceOnChannel();
                try
                {
                    var captureReady = capture.WaitForGameCapture(
                        capture.Policy.GameCaptureTimeout,
                        () => PushWarning("Still connecting game capture. Recording will start when the hook is ready."),
                        () => PushWarning(null), waitCancellation.Token);
                    if (!captureReady)
                        return false;
                }
                finally
                {
                    _captureWaitCancellation = null;
                    PushWarning(null);
                    capture.ClearSourceFromChannel();
                    waitCancellation.Dispose();
                }
            }

            if (!_recorder!.Start(resolved))
                return false;
            recordingStarted = true;

            _activeOutputPath = resolved.OutputPath;
            _currentGameId = effectiveGameId;
            _pendingMetadata = new RecordingMetadata
            {
                Game = GameList.FirstOrDefault(g => g.Id == effectiveGameId)?.Name ?? effectiveGameId,
                GameId = effectiveGameId,
                ContentType = ContentType.Recording,
                StartTime = DateTime.Now,
            };

            _sessionTracker.Start(_pendingMetadata.StartTime);
            lock (_automaticClipGate)
            {
                _automaticClipBookmarks.Clear();
                _liveHighlightRegions.Clear();
                _liveHighlightBookmarkIds.Clear();
                _recordingStartUtc = DateTime.UtcNow;
                _liveHighlightsEnabled = settings.Recording.AutomaticClipsEnabled
                    && resolved.Mode.UsesReplayBuffer();
                _liveHighlightCancellation?.Dispose();
                _liveHighlightCancellation = _liveHighlightsEnabled
                    ? new CancellationTokenSource()
                    : null;
                _liveHighlightTasks.Clear();
            }

            StartDetection(effectiveGameId);

            PushState(recording: true, effectiveGameId);
            RequestNotification(NotificationKind.RecordingStarted, "Recording started",
                string.IsNullOrWhiteSpace(effectiveGameId) ? "Tript is recording." : $"Tript is recording {effectiveGameId}.");
            return true;
        }
        finally
        {
            if (!recordingStarted)
            {
                Volatile.Write(ref _recordingProcessOwner, null);
                SetBackgroundWorkSuspendedForRecording(false);
            }
        }
    }

    internal bool StopRecording()
    {
        // StartRecording can be holding the gate while waiting for the game-capture hook. Publish
        // the stop before taking the gate, and let StartRecording handle the setup race above.
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

        var sourcePath = _activeOutputPath;
        List<Bookmark> automaticBookmarks;
        HashSet<Guid> liveBookmarkIds;
        lock (_automaticClipGate)
        {
            automaticBookmarks = _automaticClipBookmarks.ToList();
            _automaticClipBookmarks.Clear();
            liveBookmarkIds = _liveHighlightBookmarkIds.ToHashSet();
            _liveHighlightRegions.Clear();
            _liveHighlightBookmarkIds.Clear();
            _liveHighlightsEnabled = false;
        }

        var session = _sessionTracker.Stop();
        if (session is not null && _pendingMetadata is not null)
        {
            _pendingMetadata.Bookmarks = session.Bookmarks.ToList();
            WriteMetadataRecord(_pendingMetadata);

            if (sourcePath is not null && _pendingMetadata.VideoPath.Length > 0
                && _settingsStore.Load().Recording.AutomaticClipsEnabled)
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
        _currentGameId = null;
        Volatile.Write(ref _recordingProcessOwner, null);

        PushState(recording: false, null);
        RequestNotification(NotificationKind.RecordingStopped, "Recording stopped", "The recording is ready in your library.");
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

    // Persists the recording's metadata — game, start time, content type, audio tracks, the
    // automatic bookmarks and the link key back to the video — so bookmarks survive the process.
    // Written only when the recording actually exists.
    private void WriteMetadataRecord(RecordingMetadata metadata)
    {
        if (_activeOutputPath is null || !File.Exists(_activeOutputPath))
            return;

        var relative = Path.GetRelativePath(EffectiveRoot, _activeOutputPath)
            .Replace(Path.DirectorySeparatorChar, '/');
        metadata.VideoPath = relative;

        lock (_metadata.WriteGate)
        {
            // The one write allowed to replace whatever is on disk, because here the in-memory record
            // is the authoritative one: this process just made the recording.
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

    // ---- recorder construction ----

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
            // The scene's layers are composed in the session's constructor, so a changed capture
            // method or monitor needs a new session — otherwise the setting only takes effect at
            // the next app start. Reached from Idle only (StartRecording refuses otherwise).
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

        // The plugin's default colour is white (0xFFFFFFFF), which would render recordings as a white
        // canvas until a game is hooked.
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

    // Re-points the session's game-capture source at the game being recorded. win-capture keys on the
    // executable, so this is the catalogue's Executable (never the display name) with the platform
    // extension put back — normalized first, because a settings entry may spell it either way and
    // "cs2.exe.exe" hooks nothing. A no-op when the platform has no game capture (Linux) or the
    // session is the fake.
    private void RetargetGameCapture(string gameId)
    {
        if (_recorderSession is not ObsRecorderSession session)
            return;

        var name = GameCaptureName(gameId);
        var executable = OperatingSystem.IsWindows() ? $"{name}.exe" : name;
        session.RetargetGame(new ObsGameCaptureTarget(null, null, executable));
    }

    // The extension-free executable for a game, or the id itself when the catalogue does not list it.
    internal string GameCaptureName(string gameId)
    {
        var entry = GameList.FirstOrDefault(g => g.Id == gameId);
        return ExecutableNames.Normalize(entry is null ? gameId : ExecutableOf(entry));
    }

    // New recordings are scoped under <effectiveRoot>/<game>/sessions/. Existing legacy recordings
    // remain readable because catalogue classification accepts both layouts.
    private string BuildOutputPath(SettingsModel settings, string gameId)
    {
        var directory = Path.Combine(EffectiveRoot, GameFolderName(gameId), "sessions");
        Directory.CreateDirectory(directory);
        // Millisecond resolution keeps two sessions started in the same second from colliding on one file
        // name, which would overwrite the recording and its metadata record.
        var name = $"session-{DateTime.Now:yyyyMMdd-HHmmssfff}.mp4";
        return Path.Combine(directory, name);
    }

    private string ClipDirectoryForSource(string sourcePath)
    {
        var relative = Path.GetRelativePath(EffectiveRoot, sourcePath)
            .Replace(Path.DirectorySeparatorChar, '/');
        var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var sessionsIndex = Array.FindIndex(parts,
            part => part.Equals("sessions", StringComparison.Ordinal));
        if (sessionsIndex > 0)
            return Path.Combine(new[] { EffectiveRoot }
                .Concat(parts.Take(sessionsIndex))
                .Append("clips")
                .ToArray());

        return Path.Combine(EffectiveRoot, "clips");
    }

    private string HighlightsDirectoryForSource(string sourcePath)
    {
        var directory = HighlightsDirectoryPathForSource(sourcePath);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private string HighlightsDirectoryPathForSource(string sourcePath)
    {
        var relative = Path.GetRelativePath(EffectiveRoot, sourcePath)
            .Replace(Path.DirectorySeparatorChar, '/');
        var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var sessionsIndex = Array.FindIndex(parts,
            part => part.Equals("sessions", StringComparison.Ordinal));
        var directory = sessionsIndex > 0
            ? Path.Combine(new[] { EffectiveRoot }
                .Concat(parts.Take(sessionsIndex))
                .Append("highlights")
                .ToArray())
            : Path.Combine(EffectiveRoot, "highlights");
        return directory;
    }

    private string GameFolderName(string gameId)
    {
        var game = GameList.FirstOrDefault(candidate => candidate.Id == gameId)?.Name;
        return SafeDirectoryName(string.IsNullOrWhiteSpace(game) ? gameId : game);
    }

    private static string SafeDirectoryName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(value.Trim().Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(name) || name is "." or ".." ? "Unknown Game" : name;
    }
}
