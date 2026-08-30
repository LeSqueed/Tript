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

public enum NotificationKind
{
    RecordingStarted,
    RecordingStopped,
    Error,
    Recovery,
}

// Assembles every component into the running app and owns the process lifetime: the settings store
// and session tracker, the recorder plus its game detector and detection host, the three local IPC
// channels (control socket, content server, UI host), the content catalogue, and the clip pipeline.
// Every state push is a full push, so the frontend converges to a consistent view.
internal sealed partial class AppHost : IDisposable
{
    private const string StartupGameId = "Overwatch";

    private readonly AppOptions _options;
    private readonly SettingsStore _settingsStore;
    private readonly object _settingsUpdateGate = new();
    private readonly ObsRuntime? _runtime;
    private readonly RecordingSessionTracker _sessionTracker;

    // Detected once at startup, or null when the machine would not say. Offered to the settings UI
    // on every push; never persisted, because it is a fact about the machine rather than a setting.
    private readonly DisplaySize? _primaryDisplay;

    // Generated once per launch and held only in memory. Every one of the three listeners requires
    // it; see SessionToken for what that does and does not buy.
    private readonly SessionToken _token = new();

    private readonly AppController _controller;
    private readonly IpcServer _ipc;
    private readonly ContentServer _content;
    private readonly UiHost _ui;
    private readonly RecordingMetadataStore _metadata;
    private readonly ClipTitleStore _clipTitles;
    private readonly ThumbnailStore _thumbnails;
    private readonly TrashStore _trash;
    private readonly GameCatalog _gameCatalog;
    private readonly GameModelManager? _modelManager;

    // Located at most once per process: FfmpegLocator.Locate walks PATH and then runs `-version` on
    // both binaries, which is four processes, and the answer cannot change while the host runs.
    // Null means no usable ffmpeg — the library then shows placeholder cards and no durations.
    private readonly Lazy<(string Ffmpeg, string Ffprobe)?> _libraryTools = new(() =>
    {
        try
        {
            return new FfmpegLocator().Locate();
        }
        catch (FfmpegNotFoundException exception)
        {
            Console.Error.WriteLine(
                $"Tript.App: no thumbnails or durations in the library — {exception.Message}");
            return null;
        }
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    private MediaProbe? _libraryProbe;

    // Files whose duration could not be read, so a broken file is probed at most once per process.
    private readonly HashSet<string> _unprobeable = new(StringComparer.Ordinal);

    // Every transition of the recorder's own state runs under this. Three threads reach these
    // methods — the IPC dispatch pool, the game detector's timer, and the hook probe's timer — and
    // StartRecording is a check-then-act with EnsureRecorderBuilt (which disposes and nulls
    // _recorder) in the middle. Without the gate the losing thread drives a DISPOSED session into
    // libobs, or two starts cross their metadata sidecars over one file.
    //
    // A stop can hold this for up to its 10s settle, and a start that waits behind it is correct:
    // there is one recorder.
    private readonly object _recorderGate = new();
    private readonly TimeSpan _recorderStopTimeout;
    private bool _shuttingDown;

    private RecorderStateMachine? _recorder;
    private IRecorderSession? _recorderSession;
    private ObsSource? _colourSource;
    private bool _stopFinalizationPending;
    private ProcessNameGameDetector? _detector;
    private FullscreenGameDetector? _fullscreenDetector;
    private readonly GameDiscoveryService? _discovery;
    private readonly IDiscoveryFileSystem _discoveryFileSystem = new PhysicalDiscoveryFileSystem();
    private readonly object _inventoryGate = new();
    private GameInventory _inventory = new([], []);
    private readonly SemaphoreSlim _discoveryScanSemaphore = new(1, 1);
    private readonly CancellationTokenSource _discoveryCancellation = new();
    private Task _discoveryTask = Task.CompletedTask;
    private readonly object _ignoredCandidateGate = new();
    private readonly HashSet<string> _ignoredCandidatePaths = new(StringComparer.OrdinalIgnoreCase);
    private DetectionHost? _detectionHost;
    private RecordingMetadata? _pendingMetadata;
    private string? _activeOutputPath;
    private readonly object _automaticClipGate = new();
    private readonly List<Bookmark> _automaticClipBookmarks = [];
    private AutomaticClipJob? _automaticClipJob;
    private bool _backgroundWorkSuspendedForRecording;
    private CancellationTokenSource? _liveHighlightCancellation;
    private readonly List<Task> _liveHighlightTasks = [];
    private readonly List<LiveHighlightRegion> _liveHighlightRegions = [];
    private readonly HashSet<Guid> _liveHighlightBookmarkIds = [];
    private DateTime _recordingStartUtc;
    private bool _liveHighlightsEnabled;
    private CancellationTokenSource? _captureWaitCancellation;
    private int _recordingStopRequested;
    private readonly DetectedGameTracker _detectedGames = new();

    // Replaced wholesale under _gameListGate, never mutated in place: GameList is read from the
    // IPC pool, the detector's timer and the hook probe, and a reader holding the old list must
    // be able to finish enumerating it.
    private readonly object _gameListGate = new();
    private List<GameInfo> _catalogueGames = [];
    private IClipEngine? _clipEngine;
    private readonly object _sdrConversionGate = new();
    private readonly HashSet<string> _sdrConversions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reservedClipOutputs = new(StringComparer.OrdinalIgnoreCase);

    // The bin is swept at startup and once an hour after it, so a host that stays up for days still
    // honours the retention.
    private static readonly TimeSpan TrashPurgeInterval = TimeSpan.FromHours(1);

    private Timer? _trashPurgeTimer;

    private bool _disposed;

    private sealed class AutomaticClipJob
    {
        internal required string SourceSessionPath { get; init; }

        internal required int Total { get; init; }

        internal int Completed { get; set; }

        internal bool PausedByUser { get; set; }
    }

    private sealed class LiveHighlightRegion
    {
        internal required TimeSpan Start { get; set; }

        internal required TimeSpan End { get; set; }

        internal HashSet<Guid> BookmarkIds { get; } = [];

        internal bool SaveRequested { get; set; }

        internal bool Abandoned { get; set; }
    }

    // Let a detection callback at the exact post-roll boundary arrive before the replay save is
    // requested. The clip end remains the event's ten-second post-roll; this only removes the timer
    // race between the detector callback and the save task.
    private static readonly TimeSpan LiveHighlightBoundaryGrace = TimeSpan.FromSeconds(1);

    // primaryDisplay is optional: a host built without one just pushes no display resolution.
    internal AppHost(AppOptions options, SettingsStore settingsStore, ObsRuntime? runtime,
        RecordingSessionTracker sessionTracker, DisplaySize? primaryDisplay = null,
        TimeSpan? recorderStopTimeout = null, bool enableModelDelivery = false)
    {
        _options = options;
        _settingsStore = settingsStore;
        _runtime = runtime;
        _sessionTracker = sessionTracker;
        _primaryDisplay = primaryDisplay;
        _gameCatalog = GameCatalog.Load(Path.Combine(AppContext.BaseDirectory, "data", "games.json"));
        _discovery = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)
            ? GameDiscoveryService.CreateDefault(new WindowsXboxPackageProvider())
            : null;
        _recorderStopTimeout = recorderStopTimeout ?? TimeSpan.FromSeconds(10);

        if (enableModelDelivery)
        {
#if TRIPT_TRAINING
            var customRoot = TrainingPaths.InstalledModelsPath;
            bool HasCustomModel(string gameId)
            {
                var gamePath = Path.Combine(customRoot, GameModelPaths.ValidateGameId(gameId));
                return File.Exists(Path.Combine(gamePath, "model.onnx")) &&
                    File.Exists(Path.Combine(gamePath, "events.json"));
            }
#else
            Func<string, bool>? HasCustomModel = null;
#endif
            _modelManager = new GameModelManager(ActivateDownloadedModelAsync,
                bundledManifestPath: Path.Combine(AppContext.BaseDirectory, "data", "model-manifest.json"),
                hasCustomModel: HasCustomModel);
            _modelManager.StatusChanged += OnModelStatusChanged;
        }

        EffectiveRoot = Path.GetFullPath(ResolveEffectiveRoot(options, settingsStore));

        _controller = new AppController(this);
        _ipc = new IpcServer(_controller, _token, options.ControlPort, options.UiPort);
        _metadata = new RecordingMetadataStore(Path.Combine(EffectiveRoot, "metadata"));
        _clipTitles = new ClipTitleStore(Path.Combine(EffectiveRoot, "metadata"));
        _thumbnails = new ThumbnailStore(ThumbnailRootFor(EffectiveRoot), CreateThumbnailExtractor);
        _trash = new TrashStore(TrashRootFor(EffectiveRoot));
        _content = new ContentServer(EffectiveRoot, _token, _thumbnails, options.ContentPort);
        _ui = new UiHost(options.WebRoot, _token, options.UiPort);

        Directory.CreateDirectory(EffectiveRoot);
        ReloadGameList();
    }

    internal AppOptions Options => _options;

    internal SettingsStore SettingsStore => _settingsStore;

    internal event Action<SettingsModel>? SettingsChanged;

    internal event Action<bool, string?>? StateChanged;

    internal event Action<NotificationKind, string, string>? NotificationRequested;

    internal ObsRuntime? Runtime => _runtime;

    internal IpcServer Ipc => _ipc;

    internal ContentServer Content => _content;

    // The UI URL with this launch's token on it: what the desktop shell loads in-process, and what
    // the READY line prints for a headless user's own terminal. It is never passed on a command
    // line and never written to the log.
    internal string UiUrl => _token.BuildUiUrl(_options.UiPort);

    internal bool IsRecording => _recorder is not null && _recorder.Snapshot.State != RecorderState.Idle;

    internal string? CurrentGameId => _recorder is not null && _recorder.Snapshot.State != RecorderState.Idle
        ? _currentGameId
        : null;

    private string? _currentGameId;
    private string? _recordingProcessOwner;

    // The single root everything content lives under: sessions, clips, the metadata tree and the
    // content server's traversal guard all resolve against it. A configured Recording.OutputDirectory
    // wins; empty falls back to the content root, and a settings change updates it in place.
    internal string EffectiveRoot { get; private set; }

    internal bool ConvertHdrClipsToSdr => _settingsStore.Load().General.ConvertHdrClipsToSdr;

    // Why a recording directory is refused, or null when it is fine.
    //
    // The content server serves everything under this root, and it deliberately checks no Origin —
    // a media element sends none, so requiring one would break the app's own player. That is only
    // safe while the root is a folder of recordings. Pointing it at "/" or at the home directory
    // turns http://localhost:8893/api/content/<path> into a reader for the whole machine, for any
    // page open in the user's browser.
    //
    // The rule is deliberately narrow: refuse a root that CONTAINS somewhere sensitive, rather than
    // trying to enumerate what is safe. A directory the user made for recordings passes.
    internal static string? UnsafeRecordingRoot(string candidate)
    {
        if (!Path.IsPathRooted(candidate))
            return "it is not an absolute path";

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                             or PathTooLongException)
        {
            return "it is not a usable path";
        }

        if (Path.GetPathRoot(full) is { } root &&
            string.Equals(Path.TrimEndingDirectorySeparator(root), full, StringComparison.Ordinal))
        {
            return "a filesystem root would expose the whole machine over the content server";
        }

        foreach (var folder in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            SettingsFilePaths.ConfigDirectory,
        })
        {
            if (string.IsNullOrEmpty(folder))
                continue;

            var sensitive = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (IsAtOrAbove(full, sensitive))
                return $"it contains '{sensitive}', which would expose it over the content server";
        }

        return null;
    }

    // True when `candidate` IS `sensitive` or is one of its ancestors.
    private static bool IsAtOrAbove(string candidate, string sensitive)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(candidate, sensitive, comparison))
            return true;

        return sensitive.StartsWith(candidate.EndsWith(Path.DirectorySeparatorChar)
            ? candidate
            : candidate + Path.DirectorySeparatorChar, comparison);
    }

    private static string ResolveEffectiveRoot(AppOptions options, SettingsStore settingsStore)
        => ResolveEffectiveRoot(options, settingsStore.Load());

    private static string ResolveEffectiveRoot(AppOptions options, SettingsModel settings)
    {
        var configured = settings.Recording.OutputDirectory;
        return string.IsNullOrWhiteSpace(configured) ? options.ContentRoot : configured;
    }

    // Inside the metadata tree (see ThumbnailStore), in its own subdirectory so the record directory
    // stays hand-readable.
    private static string ThumbnailRootFor(string effectiveRoot) =>
        Path.Combine(effectiveRoot, "metadata", "thumbnails");

    // At the top of the recording root, so a trashed item and its records move by rename rather
    // than by copy, and so emptying the bin is one directory delete.
    private static string TrashRootFor(string effectiveRoot) =>
        Path.Combine(effectiveRoot, TrashStore.DirectoryName);

    // Null when this machine has no usable ffmpeg. Called at most once, by the thumbnail store's lazy.
    private IThumbnailExtractor? CreateThumbnailExtractor()
    {
        var tools = _libraryTools.Value;
        return tools is null ? null : new FfmpegThumbnailExtractor(tools.Value.Ffmpeg, tools.Value.Ffprobe);
    }

    // ---- lifetime ----

    public void Run()
    {
        using var shutdownRequested = new ManualResetEventSlim(false);
        Action onShutdown = shutdownRequested.Set;
        _ipc.ShutdownRequested += onShutdown;

        try
        {
            _ipc.Start();
            _content.Start();
            _ui.Start();

            // Anything already past its retention goes now, and hourly after that.
            PurgeExpiredTrash();
            _trashPurgeTimer = new Timer(_ => PurgeExpiredTrash(), null, TrashPurgeInterval, TrashPurgeInterval);

            WireAutoStart();

            // The single-line contract the smoke test waits for. It now carries the UI URL, token and
            // all: that is how a headless user reaches their own app, and the token dies with the
            // process. Console, not the log — the log is a file that outlives the launch.
            Console.WriteLine($"READY {UiUrl}");
            Console.Out.Flush();
            StartDiscoveryScan();

            // No browser is opened — the desktop shell renders the UI in its own window.
            if (_ipc.ShutdownWasRequested)
                shutdownRequested.Set();
            WaitForShutdown(shutdownRequested);
        }
        finally
        {
            _ipc.ShutdownRequested -= onShutdown;
        }
    }

    private static void WaitForShutdown(ManualResetEventSlim shutdownRequested)
    {
        while (!shutdownRequested.IsSet)
            Thread.Sleep(100);

        Console.WriteLine("SHUTDOWN");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Refuse new starts before anything is torn down. The detector's Dispose deliberately does
        // not block behind an in-flight handler, so a GameStarted can still arrive after it returns.
        _captureWaitCancellation?.Cancel();
        _discoveryCancellation.Cancel();
        lock (_automaticClipGate)
        {
            if (_automaticClipJob is not null)
                _automaticClipJob.PausedByUser = false;
            _backgroundWorkSuspendedForRecording = false;
            Monitor.PulseAll(_automaticClipGate);
        }
#if TRIPT_TRAINING
        _trainingRunner.Cancel();
        _trainingCancellation?.Cancel();
#endif
        lock (_recorderGate)
            _shuttingDown = true;

        try
        {
            _discoveryTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "AppHost: launcher game discovery did not settle cleanly during shutdown.");
        }

        _trashPurgeTimer?.Dispose();
        _detectionHost?.Dispose();
        _detector?.Dispose();
        _fullscreenDetector?.Dispose();

        if (_modelManager is not null)
        {
            _modelManager.StatusChanged -= OnModelStatusChanged;
            _modelManager.Dispose();
        }

        // Preserve recording metadata when possible, but never let a dead recorder block shutdown.
        try
        {
            StopRecording();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Tript.App: recording cleanup failed during shutdown: {exception.Message}");
        }

        RecorderStateMachine? recorder;
        IRecorderSession? recorderSession;
        ObsSource? colourSource;
        ObsRuntime? runtime;
        lock (_recorderGate)
        {
            recorder = _recorder;
            recorderSession = _recorderSession;
            colourSource = _colourSource;
            runtime = _runtime;
            _recorder = null;
            _recorderSession = null;
            _colourSource = null;
        }

        if (recorder?.Snapshot.State == RecorderState.Stopping)
        {
            // A native muxer can remain stopped-but-unacknowledged indefinitely. Keep the native
            // session alive and finish this disposal after its callback instead of blocking app exit
            // or releasing the runtime underneath the callback.
            ThreadPool.QueueUserWorkItem(_ => DisposeRecorderResources(
                recorder, recorderSession, colourSource, runtime));
        }
        else
        {
            DisposeRecorderResources(recorder, recorderSession, colourSource, runtime);
        }

        _ipc.Dispose();
        _content.Dispose();
        _ui.Dispose();
    }

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

        // The recorder marshals the transition onto the thread it was created on; the IPC thread is that
        // thread for the real recorder, and the fake raises the signal synchronously inside Stop.
        var deadline = DateTime.UtcNow + _recorderStopTimeout;
        while (_recorder.Snapshot.State != RecorderState.Idle)
        {
            if (DateTime.UtcNow > deadline)
            {
                _stopFinalizationPending = true;
                var recorder = _recorder;
                ThreadPool.QueueUserWorkItem(_ => CompletePendingStop(recorder!));
                Log.Warning("AppHost: recording output did not finish stopping within {Timeout}; leaving the recording in Stopping state until its callback arrives",
                    _recorderStopTimeout);
                return false;
            }
            Thread.Sleep(20);
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

            Thread.Sleep(20);
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
                    QueueAutomaticClips(sourcePath, _pendingMetadata.VideoPath, unsavedBookmarks);
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

    // ---- lifecycle no-ops (the commands the alpha accepts but does not implement) ----

    internal bool ToggleFullscreen(bool enabled) => true;

    internal void CheckForUpdates()
    {
        // No update feed in the alpha.
    }

    internal void RefreshStorageStats()
    {
        // No storage-stats surface yet.
    }

    internal void OpenLogsLocation()
    {
        // No log-file surface yet.
    }

    internal void MigrateContent()
    {
        // No migration needed for a fresh install.
    }

    // ---- native folder picker ----

    // The seam the desktop shell installs: opens a native folder picker and returns the chosen
    // directory, or null when the user cancels. The headless host has no window, so it stays null and
    // RequestVideoLocation is a no-op.
    internal Func<string?>? FolderPicker { get; set; }

    // The training picker returns a path to the UI instead of persisting it as a recording setting.
    internal Func<string?>? TrainingFolderPicker { get; set; }

    internal void RequestTrainingFolder()
    {
        var picker = TrainingFolderPicker;
        if (picker is null)
        {
            PushError("Browse is only available in the desktop shell.");
            return;
        }

        try
        {
            var path = picker();
            if (!string.IsNullOrWhiteSpace(path))
            {
                _ipc.Broadcast("trainingFolderSelected", JsonSerializer.SerializeToElement(
                    new { path }, Wire.Options));
            }
            else
            {
                _ipc.Broadcast("trainingFolderCancelled", JsonSerializer.SerializeToElement(
                    new { }, Wire.Options));
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Tript.App: the training folder picker failed: {exception.Message}");
            PushError($"The training folder picker could not be opened: {exception.Message}");
        }
    }

    // Applies the picked directory through the normal settings path, so the field updates exactly as
    // if the user had typed it. Cancelling is a no-op.
    internal void RequestVideoLocation()
    {
        var picker = FolderPicker;
        if (picker is null)
            return;

        string? path;
        try
        {
            path = picker();
        }
        catch (Exception exception)
        {
            // A picker failure (no native dialog, a refused GTK loop) must not tear down the IPC channel.
            Console.Error.WriteLine($"Tript.App: the folder picker failed: {exception.Message}");
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
            return;

        var patch = JsonSerializer.SerializeToElement(new
        {
            recording = new
            {
                outputDirectory = path,
            },
        });
        UpdateSettings(patch);
    }

    // ---- native game executable picker ----

    internal Func<string?>? GameExecutablePicker { get; set; }

    internal void RequestGameExecutable(string requestId)
    {
        var picker = GameExecutablePicker;
        string? pickedPath;
        if (picker is null)
        {
            pickedPath = null;
        }
        else
        {
            try
            {
                pickedPath = picker();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Tript.App: the game executable picker failed: {exception.Message}");
                pickedPath = null;
            }
        }

        var normalized = NormalizePickedExecutable(pickedPath);
        if (normalized is not null)
        {
            _ipc.Broadcast("selectedGameExecutable", JsonSerializer.SerializeToElement(
                new { requestId, filePath = normalized }, Wire.Options));
            return;
        }

        if (!string.IsNullOrWhiteSpace(pickedPath))
            PushError("That is not an executable file; choose a .exe before saving.");
        _ipc.Broadcast("selectedGameExecutable", JsonSerializer.SerializeToElement(
            new { requestId, filePath = (string?)null }, Wire.Options));
    }

    internal static string? NormalizePickedExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string normalized;
        try
        {
            normalized = Path.GetFullPath(path.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or IOException
            or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (!File.Exists(normalized))
            return null;

        if (OperatingSystem.IsWindows()
            && !string.Equals(Path.GetExtension(normalized), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return normalized;
    }

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
        var normalizedExecutable = ProcessNameGameDetector.NormalizeProcessName(executable);

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
            if (path.Length == 0)
                continue;
            var file = Path.GetFileName(path);
            if (string.Equals(ProcessNameGameDetector.NormalizeProcessName(file), normalizedExecutable,
                StringComparison.OrdinalIgnoreCase))
            {
                return ProcessNameGameDetector.NormalizePath(path);
            }
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

            var executable = ProcessNameGameDetector.NormalizeProcessName(ExecutableOf(game));
            if (string.Equals(executable, processName, StringComparison.OrdinalIgnoreCase))
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

        var normalized = ProcessNameGameDetector.NormalizeProcessName(gameName);
        return GameList.FirstOrDefault(game =>
            string.Equals(ProcessNameGameDetector.NormalizeProcessName(ExecutableOf(game)), normalized,
                StringComparison.OrdinalIgnoreCase))?.Id;
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
        bookmark.IsAutomaticClipCandidate = true;
        LiveHighlightRegion? regionToSchedule = null;
        CancellationToken token = default;
        lock (_automaticClipGate)
        {
            _automaticClipBookmarks.Add(bookmark);

            if (!_liveHighlightsEnabled || _liveHighlightCancellation is null)
                return;

            var start = bookmark.Time > AutomaticClipPlanner.PreRoll
                ? bookmark.Time - AutomaticClipPlanner.PreRoll
                : TimeSpan.Zero;
            var existing = _liveHighlightRegions.FirstOrDefault(region =>
                !region.SaveRequested && bookmark.Time <= region.End);
            if (existing is not null)
            {
                existing.End = existing.End > bookmark.Time + AutomaticClipPlanner.PostRoll
                    ? existing.End
                    : bookmark.Time + AutomaticClipPlanner.PostRoll;
                existing.BookmarkIds.Add(bookmark.Id);
                return;
            }

            regionToSchedule = new LiveHighlightRegion
            {
                Start = start,
                End = bookmark.Time + AutomaticClipPlanner.PostRoll,
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
                TimeSpan delay;
                lock (_automaticClipGate)
                {
                    if (region.SaveRequested || !_liveHighlightsEnabled)
                        return;
                    delay = _recordingStartUtc + region.End + LiveHighlightBoundaryGrace - DateTime.UtcNow;
                }

                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

                lock (_automaticClipGate)
                {
                    if (region.SaveRequested || !_liveHighlightsEnabled)
                        return;
                    if (_recordingStartUtc + region.End + LiveHighlightBoundaryGrace > DateTime.UtcNow)
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

    // ---- settings ----

    internal bool UpdateSettings(JsonElement? patch, string? requestId = null)
    {
        lock (_settingsUpdateGate)
            return UpdateSettingsLocked(patch, requestId);
    }

    private bool UpdateSettingsLocked(JsonElement? patch, string? requestId)
    {
        if (patch is null)
        {
            PushSettingsUpdateResult(requestId, false, "The settings update was empty.");
            return false;
        }

        var previousGameKeys = _settingsStore.Load().Game.GameList
            .Select(game => (game.Id, game.Executable, game.ExecutablePath))
            .ToList();
        var previousGameNames = _settingsStore.Load().Game.GameList
            .Select(game => (game.Id, game.Name))
            .ToList();
        SettingsModel settings;
        string? failure;
        bool saved;
        try
        {
            saved = _settingsStore.TryUpdate(candidate =>
            {
                ApplyPatch(candidate, patch.Value);
                if (!ValidateGameList(candidate.Game.GameList, out var validationError,
                    requireExistingExecutables: PatchUpdatesGameList(patch.Value)))
                    return validationError;

                string effectiveRoot;
                try
                {
                    effectiveRoot = Path.GetFullPath(ResolveEffectiveRoot(_options, candidate));
                }
                catch (Exception exception) when (exception is ArgumentException or IOException
                    or NotSupportedException or PathTooLongException)
                {
                    return "the recording directory is not a usable path.";
                }

                return UnsafeRecordingRoot(effectiveRoot) is { } refusal
                    ? $"the recording directory was refused because {refusal}."
                    : CreateRecordingRoot(effectiveRoot);
            }, out settings, out failure);
        }
        catch (JsonException exception)
        {
            settings = _settingsStore.Load();
            failure = exception.Message;
            saved = false;
        }

        if (!saved)
        {
            var error = $"Those settings were not saved: {failure ?? "the settings file could not be written."}";
            PushError(error);
            PushSettings();
            PushSettingsUpdateResult(requestId, false, error);
            return true;
        }

        ReloadGameList();
        var gameKeysNow = settings.Game.GameList
            .Select(game => (game.Id, game.Executable, game.ExecutablePath))
            .ToList();
        var gameNamesNow = settings.Game.GameList
            .Select(game => (game.Id, game.Name))
            .ToList();
        var gameKeysChanged = !previousGameKeys.SequenceEqual(gameKeysNow);
        var gameNamesChanged = !previousGameNames.SequenceEqual(gameNamesNow);
        if (gameKeysChanged || gameNamesChanged)
        {
            PushGameList();
            if (gameKeysChanged)
                RebuildDetectionTargets();
        }
        if (gameNamesChanged)
            PushContent();

        var effectiveRoot = Path.GetFullPath(ResolveEffectiveRoot(_options, settings));
        if (!string.Equals(effectiveRoot, EffectiveRoot, StringComparison.Ordinal))
        {
            EffectiveRoot = effectiveRoot;
            _content.UpdateRoot(effectiveRoot);
            _metadata.UpdateRoot(Path.Combine(effectiveRoot, "metadata"));
            _clipTitles.UpdateRoot(Path.Combine(effectiveRoot, "metadata"));
            _thumbnails.UpdateRoot(ThumbnailRootFor(effectiveRoot));
            _trash.UpdateRoot(TrashRootFor(effectiveRoot));
        }

        SettingsChanged?.Invoke(settings);
        PushSettings();
        PushSettingsUpdateResult(requestId, true, null);
        return true;
    }

    private static string? CreateRecordingRoot(string path)
    {
        Directory.CreateDirectory(path);
        return null;
    }

    private void PushSettingsUpdateResult(string? requestId, bool success, string? error)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            return;

        _ipc.Broadcast("settingsUpdateResult", JsonSerializer.SerializeToElement(new
        {
            requestId,
            success,
            error,
        }, Wire.Options));
    }

    private static void ApplyPatch(SettingsModel settings, JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in patch.EnumerateObject())
        {
            switch (property.Name)
            {
                case "recording" when property.Value.ValueKind == JsonValueKind.Object:
                    ApplyObjectPatch(settings.Recording, property.Value);
                    break;
                case "buffer" when property.Value.ValueKind == JsonValueKind.Object:
                    ApplyObjectPatch(settings.Buffer, property.Value);
                    break;
                case "audio" when property.Value.ValueKind == JsonValueKind.Object:
                    ApplyObjectPatch(settings.Audio, property.Value);
                    break;
                case "capture" when property.Value.ValueKind == JsonValueKind.Object:
                    ApplyObjectPatch(settings.Capture, property.Value);
                    break;
                case "game" when property.Value.ValueKind == JsonValueKind.Object:
                    ApplyObjectPatch(settings.Game, property.Value);
                    break;
                case "general" when property.Value.ValueKind == JsonValueKind.Object:
                    ApplyObjectPatch(settings.General, property.Value);
                    break;
            }
        }
    }

    private static bool PatchUpdatesGameList(JsonElement patch) =>
        patch.ValueKind == JsonValueKind.Object
        && patch.TryGetProperty("game", out var game)
        && game.ValueKind == JsonValueKind.Object
        && game.TryGetProperty("gameList", out _);

    private static void ApplyObjectPatch(object page, JsonElement patch)
    {
        // The page objects carry JsonExtensionData, so round-tripping the page through the serializer
        // with the patch merged preserves unknown keys.
        var current = JsonSerializer.Serialize(page, SettingsSerialization.Options);
        var merged = MergeObjects(JsonDocument.Parse(current).RootElement, patch);
        var clone = JsonSerializer.Deserialize(merged, page.GetType(), SettingsSerialization.Options);
        if (clone is null)
            return;

        foreach (var property in page.GetType().GetProperties().Where(p => p.CanWrite))
        {
            var value = clone.GetType().GetProperty(property.Name)?.GetValue(clone);
            property.SetValue(page, value);
        }

        if (page is GeneralSettings general)
            SettingsSerialization.RemoveRemovedGeneralProperties(general);
    }

    private static string MergeObjects(JsonElement baseObject, JsonElement patch)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteMergedObject(writer, baseObject, patch);
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteMergedObject(Utf8JsonWriter writer, JsonElement baseObject, JsonElement patch)
    {
        writer.WriteStartObject();

        foreach (var property in baseObject.EnumerateObject())
        {
            if (!patch.TryGetProperty(property.Name, out var replacement))
            {
                property.WriteTo(writer);
                continue;
            }

            writer.WritePropertyName(property.Name);
            if (property.Value.ValueKind == JsonValueKind.Object && replacement.ValueKind == JsonValueKind.Object)
                WriteMergedObject(writer, property.Value, replacement);
            else
                replacement.WriteTo(writer);
        }

        foreach (var property in patch.EnumerateObject())
        {
            if (baseObject.TryGetProperty(property.Name, out _))
                continue;

            property.WriteTo(writer);
        }

        writer.WriteEndObject();
    }

    // ---- game list ----

    internal void ReloadGameList()
    {
        var games = AppOptions.LoadCatalogue(_settingsStore.Load(), _gameCatalog, _options.GameListJson);
        AttachDiscoveredProcessPaths(games);
        lock (_gameListGate)
            _catalogueGames = games;
    }

    // The frontend sees where a packaged game is installed once the launcher inventory confirms it:
    // the exact path is what the detection targets pin, so the wire list and the targets agree.
    private void AttachDiscoveredProcessPaths(List<GameInfo> games)
    {
        GameInventory inventory;
        lock (_inventoryGate)
            inventory = _inventory;
        if (inventory.Games.IsDefaultOrEmpty)
            return;

        foreach (var game in games)
        {
            if (!game.BuiltIn || !string.IsNullOrWhiteSpace(game.ExecutablePath))
                continue;

            var discovered = DiscoveredProcessPath(game.Id, game.Executable ?? string.Empty);
            if (discovered is not null)
                game.ExecutablePath = discovered;
        }
    }

    internal bool ValidateGameList(IReadOnlyList<GameSetting> gameList, out string? failure,
        bool requireExistingExecutables = false)
    {
        failure = null;
        var packagedIds = _gameCatalog.Entries
            .Select(entry => entry.GameId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gameIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var customPaths = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

        foreach (var game in gameList)
        {
            if (string.IsNullOrWhiteSpace(game.Id))
            {
                failure = "a game is missing its identity.";
                return false;
            }

            if (!gameIds.Add(game.Id))
            {
                failure = $"two games share the identity '{game.Id}'.";
                return false;
            }

            if (packagedIds.Contains(game.Id))
            {
                if (!string.IsNullOrWhiteSpace(game.ExecutablePath))
                {
                    failure = $"'{game.Id}' is a packaged game; its executable identity cannot be changed.";
                    return false;
                }

                continue;
            }

            try
            {
                GameModelPaths.ValidateGameId(game.Id);
            }
            catch (ArgumentException)
            {
                failure = $"'{game.Id}' is not a safe game identity.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(game.Name))
            {
                failure = "a custom game is missing its name.";
                return false;
            }

            var path = game.ExecutablePath?.Trim();
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            {
                failure = $"'{game.Id}' needs an exact absolute executable path.";
                return false;
            }

            string normalizedPath;
            try
            {
                normalizedPath = Path.GetFullPath(path);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException
                or NotSupportedException or PathTooLongException)
            {
                failure = $"'{game.Id}' has an unusable executable path.";
                return false;
            }

            if (!customPaths.Add(normalizedPath))
            {
                failure = $"two custom games use the same executable: '{path}'.";
                return false;
            }

            if (requireExistingExecutables && !File.Exists(normalizedPath))
            {
                failure = $"'{game.Id}' points to an executable that does not exist.";
                return false;
            }

            if (OperatingSystem.IsWindows()
                && !string.Equals(Path.GetExtension(normalizedPath), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                failure = $"'{game.Id}' must point to an .exe.";
                return false;
            }
        }

        return true;
    }

    // A plain read. The getter used to reload whenever the catalogue was empty, which made a user who
    // deleted every game entry re-read the settings file on every access — from every thread, while
    // clearing and refilling the one list the other threads were enumerating. The catalogue is loaded
    // at startup and reloaded when the settings that define it change, which is the only time it can
    // differ.
    internal List<GameInfo> GameList
    {
        get
        {
            lock (_gameListGate)
                return _catalogueGames;
        }
    }

    // ---- pushes ----

    internal void PushState(bool recording, string? gameId)
    {
        var representedGameId = gameId ?? (!recording ? CurrentDetectedGameId() : null);
        var game = representedGameId is null ? null : new GameInfo
        {
            Id = representedGameId,
            Name = GameList.FirstOrDefault(g => g.Id == representedGameId)?.Name ?? representedGameId,
            Detected = DetectedProcessFor(representedGameId) is not null,
        };

        AutomaticClipJob? automaticClipJob;
        lock (_automaticClipGate)
            automaticClipJob = _automaticClipJob;

        _ipc.Broadcast("state", JsonSerializer.SerializeToElement(new
        {
            state = new
            {
                recording,
                game,
                // When the current recording started, in unix seconds like every other time on this
                // wire. Without it a UI that connects mid-session can only count from the moment it
                // connected, which for an 8-hour recording is a confidently wrong number — worse
                // than showing none.
                startedAt = recording ? DateTimeToUnixSeconds(_pendingMetadata?.StartTime ?? default) : null,
                automaticClips = automaticClipJob is null ? null : new
                {
                    active = true,
                    paused = automaticClipJob.PausedByUser || BackgroundWorkSuspendedForRecording,
                    sourceSessionPath = automaticClipJob.SourceSessionPath,
                    completed = automaticClipJob.Completed,
                    total = automaticClipJob.Total,
                },
            },
        }, Wire.Options));
        StateChanged?.Invoke(recording, representedGameId);
    }

    // The machine's active WASAPI endpoints, inputs first then outputs. Each entry carries its
    // direction so the routing can pick the matching capture type: an input endpoint becomes a
    // wasapi_input_capture, an output endpoint a wasapi_output_capture.
    private static IReadOnlyList<AudioDeviceSetting> EnumerateAudioDevices()
    {
        try
        {
            return WasapiDeviceEnumerator.EnumerateInputDevices()
                .Concat(WasapiDeviceEnumerator.EnumerateOutputDevices())
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    internal void PushSettings()
    {
        var settings = _settingsStore.Load();
        var settingsNode = JsonSerializer.SerializeToNode(settings, SettingsSerialization.Options);
        // A fact about this machine, not a persisted setting, so it is settled per push and a device
        // unplugged after a save is not stuck in the settings file. Injected into the serialized element
        // only; the live model that Save() would persist is never touched.
        if (settingsNode?["audio"] is JsonObject audioNode)
            audioNode["devices"] = JsonSerializer.SerializeToNode(EnumerateAudioDevices(), SettingsSerialization.Options);
        var settingsElement = JsonSerializer.Deserialize<JsonElement>(
            settingsNode?.ToJsonString() ?? "{}", SettingsSerialization.Options);
        var displays = EnumerateDisplays();
        _ipc.Broadcast("settings", JsonSerializer.SerializeToElement(new
        {
            settings = settingsElement,
            // Only probeable when libobs is loaded — the fake-recorder host never starts it, and a P/Invoke
            // there would segfault rather than answer — so the list is absent (null) and the frontend falls
            // back to the current value plus obs_x264.
            availableEncoders = _runtime is null ? null : ObsEncoderPolicy.EnumerateUsableEncoderIds(),
            // A sibling of `settings` for the same reason the encoder list is one: RecordingSettings carries
            // JsonExtensionData, so a field nested under `recording` would be round-tripped straight into the
            // settings file on the next save. Null when detection failed.
            displayResolution = _primaryDisplay is { IsUsable: true } display
                ? (object?)new { width = display.Width, height = display.Height }
                : null,
            // The monitors this machine offers, and — when the saved one is not among them — which
            // one is standing in. Machine facts, siblings of `settings` for the same reason the
            // encoder list is one; UpdateSettings must never write either back.
            availableDisplays = displays?.Select(monitor => new
            {
                id = monitor.Id,
                name = monitor.Name,
                width = monitor.Width,
                height = monitor.Height,
                primary = monitor.Primary,
            }).ToList(),
            displayFallbackWarning = BuildDisplayFallbackWarning(settings.Capture, displays),
        }, Wire.Options));
    }

    // The monitors the display-capture source itself accepts, or null when the host cannot ask —
    // no libobs (the fake-recorder host), no registered display-capture type, or a plugin that
    // refused. Enumerated through a throwaway instance because the type-level property probe can
    // crash for a capture source, and per push because a monitor can be unplugged while we run.
    private IReadOnlyList<ObsDisplay>? EnumerateDisplays()
    {
        if (_runtime is null || ObsCaptureSource.FindDisplayCaptureId() is not { } displayId)
            return null;

        try
        {
            using var probe = ObsSource.CreatePrivate(displayId, "app display probe");
            return ObsCaptureSource.EnumerateDisplays(probe);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "AppHost: {DisplayId} would not enumerate its monitors.", displayId);
            return null;
        }
    }

    // Set only when the saved monitor is genuinely absent from a list we could read: a null list is
    // "could not ask", which is not evidence that anything is missing. The preference itself is
    // never rewritten — replugging the monitor must restore the user's choice by itself.
    private static object? BuildDisplayFallbackWarning(CaptureSettings capture, IReadOnlyList<ObsDisplay>? displays)
    {
        if (displays is null || string.IsNullOrEmpty(capture.Display))
            return null;

        var resolution = ObsCaptureSource.ResolveDisplay(displays, capture.Display);
        if (!resolution.RequestedMissing)
            return null;

        return new
        {
            requestedId = capture.Display,
            requestedLabel = capture.DisplayLabel,
            usingId = resolution.Selected?.Id,
            usingLabel = resolution.Selected?.Name,
        };
    }

    internal void PushGameList()
    {
        var element = JsonSerializer.SerializeToElement(GameList, Wire.Options);
        _ipc.Broadcast("gameList", element);
    }

    // ---- content operations ----

    internal void DeleteContent(DeleteContentParameters? parameters)
    {
        if (parameters is null || string.IsNullOrEmpty(parameters.FileName))
            return;

        DeleteOne(parameters, parameters.Permanent);
        PushContent();
        PushTrash();
    }

    internal void DeleteMultipleContent(DeleteMultipleContentParameters? parameters)
    {
        if (parameters?.Items is null)
            return;

        foreach (var item in parameters.Items)
        {
            if (string.IsNullOrEmpty(item.FileName))
                continue;
            DeleteOne(item, parameters.Permanent || item.Permanent);
        }

        PushContent();
        PushTrash();
    }

    // One item, without the pushes: a batch delete pushes once at the end rather than once per item.
    private void DeleteOne(DeleteContentParameters item, bool permanent)
    {
        // Resolve against the root even when the file is missing: a delete for a video whose file was
        // removed out-of-band must still drop the metadata record. ResolveContentFile refuses paths with
        // no file on disk, so use the traversal-safe resolver directly.
        var target = _content.ResolveWithinRoot(item.FileName);
        if (target is null)
            return;

        var fileName = Path.GetFileName(target);
        if (permanent)
        {
            UnlinkContent(target, fileName);
            return;
        }

        // The records are read before they move, so the entry can be listed with the title, game and
        // length the library was showing.
        var relative = RelativeToRoot(target);
        var seed = new TrashEntryRecord
        {
            ContentType = ResolveContentType(item.ContentType, relative),
            FileName = fileName,
            OriginalPath = relative,
            DeletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

        var metadata = _metadata.Load(fileName);
        if (metadata is not null)
        {
            seed.Title = string.IsNullOrWhiteSpace(metadata.Title) ? null : metadata.Title;
            seed.Game = string.IsNullOrWhiteSpace(metadata.Game) ? null : metadata.Game;
            seed.DurationSeconds = metadata.DurationSeconds;
        }

        var clipRecord = _clipTitles.LoadRecord(fileName);
        if (clipRecord is not null)
        {
            seed.Title ??= string.IsNullOrWhiteSpace(clipRecord.Title) ? null : clipRecord.Title;
            seed.DurationSeconds ??= clipRecord.DurationSeconds;
        }

        seed.Title ??= Path.GetFileNameWithoutExtension(fileName);

        var files = new List<TrashedFile>();
        if (File.Exists(target))
        {
            seed.FileSizeBytes = SafeLength(new FileInfo(target));
            files.Add(new TrashedFile(target, relative));
        }

        AddIfPresent(files, _metadata.PathFor(fileName));
        AddIfPresent(files, _clipTitles.PathFor(fileName));
        AddIfPresent(files, _thumbnails.PathFor(fileName));

        if (files.Count == 0)
            return;

        if (_trash.Add(files, seed, out var failure) is null)
        {
            Console.Error.WriteLine($"Tript.App: could not move '{target}' to the trash: {failure}");
            PushError($"'{fileName}' could not be moved to the trash ({failure}), so it was left where it is.");
        }
    }

    // The permanent path, unchanged: the video and every record keyed to it are unlinked outright.
    private void UnlinkContent(string target, string fileName)
    {
        try
        {
            File.Delete(target);
            // Cascade delete, so the metadata/ tree never keeps an orphaned record. The cached thumbnail goes
            // too: an image left behind would both leak the deleted recording's contents and be inherited by
            // the next recording to reuse the name.
            var metadataDeleted = _metadata.Delete(fileName);
            var clipRecordDeleted = _clipTitles.Delete(fileName);
            var thumbnailDeleted = _thumbnails.Delete(fileName);
            if (!metadataDeleted || !clipRecordDeleted || !thumbnailDeleted)
            {
                PushError(
                    $"'{fileName}' was deleted, but one or more associated records could not be removed. " +
                    "Check the metadata and thumbnail folders before reusing the name.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Tript.App: could not delete '{target}': {exception.Message}");
            PushError($"'{fileName}' could not be deleted ({exception.Message}).");
        }
    }

    private void AddIfPresent(List<TrashedFile> files, string path)
    {
        if (File.Exists(path))
            files.Add(new TrashedFile(path, RelativeToRoot(path)));
    }

    private string RelativeToRoot(string absolutePath) =>
        Path.GetRelativePath(EffectiveRoot, absolutePath).Replace(Path.DirectorySeparatorChar, '/');

    // The four names the wire knows. An item whose type the frontend did not name is classified the
    // way the library classifies it, by its top-level directory.
    private static readonly HashSet<string> WireContentTypes =
        new(StringComparer.Ordinal) { "recording", "clip", "highlight", "buffer" };

    private static string ResolveContentType(string? requested, string relativePath)
    {
        if (requested is not null && WireContentTypes.Contains(requested))
            return requested;
        return TopLevelDirectory(relativePath) is "clips" or "highlights" ? "clip" : "recording";
    }

    // ---- trash ----

    private int RetentionHours => _settingsStore.Load().Recording.TrashRetentionHours;

    // The bin as the wire spells it. purgeAt is derived from the retention in force right now, so
    // changing the setting re-dates every entry instead of pinning it to the old window.
    internal List<TrashEntry> TrashEntries()
    {
        var retentionHours = RetentionHours;
        return _trash.List().Select(entry => new TrashEntry
        {
            Id = entry.Id,
            ContentType = entry.ContentType,
            FileName = entry.FileName,
            Title = entry.Title,
            Game = entry.Game,
            DurationSeconds = entry.DurationSeconds,
            FileSizeBytes = entry.FileSizeBytes,
            DeletedAt = entry.DeletedAt,
            PurgeAt = retentionHours <= 0 ? 0 : entry.DeletedAt + retentionHours * 3600L,
        }).ToList();
    }

    internal void PushTrash()
    {
        _ipc.Broadcast("trash", JsonSerializer.SerializeToElement(new
        {
            entries = TrashEntries(),
            retentionHours = RetentionHours,
        }, Wire.Options));
    }

    internal void RestoreTrash(RestoreTrashParameters? parameters)
    {
        if (parameters?.EntryIds is null)
            return;

        foreach (var entryId in parameters.EntryIds)
        {
            var result = _trash.Restore(entryId, EffectiveRoot);
            if (result.Failure is not null)
            {
                Console.Error.WriteLine($"Tript.App: could not restore '{entryId}': {result.Failure}");
                PushError($"That item could not be restored ({result.Failure}).");
                continue;
            }

            if (result.Renamed)
            {
                // The name changed, so the metadata record's own link back to the video has to change
                // with it — and the user has to be told which name to look for.
                if (RelinkRestoredMetadata(result.FileName!, result.RestoredAs!))
                    PushError($"'{result.FileName}' was restored as '{result.RestoredAs}' — a file with its own name was already there.");
                else
                    PushError($"'{result.FileName}' was restored as '{result.RestoredAs}', but its metadata link could not be updated. Check the metadata folder.");
            }

            if (result.KeptInTrash > 0)
            {
                PushError(
                    $"'{result.FileName}' was restored, but {result.KeptInTrash} of its saved records " +
                    "could not be put back and are still in the trash.");
            }
        }

        PushContent();
        PushTrash();
    }

    // Points a restored recording's metadata record back at the file it came back as. The record
    // has already moved to the new key; only the videoPath inside it is stale. An unreadable record
    // is left exactly as it is — a rewritten one would cost the game and the bookmarks.
    private bool RelinkRestoredMetadata(string originalFileName, string restoredFileName)
    {
        lock (_metadata.WriteGate)
        {
        var existing = _metadata.Read(restoredFileName);
        if (existing.State != StoredRecordState.Loaded)
            return existing.State == StoredRecordState.Absent;

        var record = existing.Record!;
        if (!string.Equals(Path.GetFileName(record.VideoPath), originalFileName, StringComparison.OrdinalIgnoreCase))
            return false;
        var separator = record.VideoPath.LastIndexOf('/');
        record.VideoPath = separator < 0
            ? restoredFileName
            : $"{record.VideoPath[..separator]}/{restoredFileName}";
        return _metadata.Save(record);
        }
    }

    internal void PurgeTrash(PurgeTrashParameters? parameters)
    {
        // Null is a frame whose parameters could not be parsed, never "no parameters" — the dispatch
        // substitutes an explicit object for that. Refusing it matters more here than anywhere else:
        // this is the one command whose empty case is destructive.
        if (parameters is null)
            return;

        // No entryIds at all means the whole bin; an explicit (possibly empty) list means exactly
        // those entries.
        var entryIds = parameters.EntryIds ?? _trash.List().Select(entry => entry.Id).ToList();

        foreach (var entryId in entryIds)
        {
            if (_trash.Purge(entryId, out var failure))
                continue;
            Console.Error.WriteLine($"Tript.App: could not purge '{entryId}': {failure}");
            PushError($"That item could not be removed from the trash ({failure}).");
        }

        PushContent();
        PushTrash();
    }

    // Drops every entry whose retention window has closed. A retention of zero or less disables it
    // altogether: the bin then keeps what it holds until it is emptied by hand.
    internal void PurgeExpiredTrash()
    {
        try
        {
            var retentionHours = RetentionHours;
            if (retentionHours <= 0)
                return;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var purged = 0;
            foreach (var entry in _trash.List())
            {
                if (entry.DeletedAt + retentionHours * 3600L > now)
                    continue;
                if (_trash.Purge(entry.Id, out _))
                    purged++;
            }

            if (purged == 0)
                return;

            PushContent();
            PushTrash();
        }
        catch (Exception exception)
        {
            // This runs on a timer thread; an escape here would be an unhandled exception.
            Console.Error.WriteLine($"Tript.App: the trash could not be swept: {exception.Message}");
        }
    }

    internal void RenameContent(RenameContentParameters? parameters)
    {
        if (parameters is null || string.IsNullOrEmpty(parameters.FileName))
            return;

        var target = ResolveContentFile(parameters.FileName);
        if (target is null)
            return;

        var relative = Path.GetRelativePath(EffectiveRoot, target).Replace(Path.DirectorySeparatorChar, '/');
        var fileName = Path.GetFileName(target);

        // Which store the title lands in is decided by the path, not by the wire's contentType.
        // ListContent classifies the same way and reads a clip's title from ClipTitleStore, so a
        // clip renamed into a RecordingMetadata record would write something nothing ever reads —
        // and the wire's contentType defaults to "recording" whether or not the caller meant it.
        if (TopLevelDirectory(relative) is "clips" or "highlights")
        {
            RenameClip(fileName, parameters.Title);
            return;
        }

        // The title is stored on the video's metadata record; a video with no record yet gets one.
        // A record that exists but could not be READ is not a record to replace — writing a fresh
        // one would trade the recording's game and bookmarks for a title.
        lock (_metadata.WriteGate)
        {
        var existing = _metadata.Read(fileName);
        if (existing.MustNotBeOverwritten)
        {
            Console.Error.WriteLine(
                $"Tript.App: '{fileName}' has a metadata record that could not be read " +
                $"({existing.Failure}); the rename is refused rather than replacing it.");
            PushError(
                "The recording title could not be saved — this recording's metadata record could not be read, and overwriting it would lose its game and bookmarks.");
            return;
        }

        var metadata = existing.Record ?? new RecordingMetadata
        {
            VideoPath = relative,
        };
        metadata.Title = parameters.Title;

        if (!_metadata.Save(metadata))
        {
            // Surface the failure and leave the list as it was — no content push, so the old title stays on
            // screen rather than a title that was never saved.
            PushError("The recording title could not be saved — check the recording folder is writable.");
            return;
        }
        PushContent();
        }
    }

    private void RenameClip(string fileName, string title)
    {
        lock (_clipTitles.WriteGate)
        {
        // Same discipline as the recording path: a record that exists and could not be read is left
        // alone rather than replaced, because the record also carries the clip's measured duration.
        var existing = _clipTitles.Read(fileName);
        if (existing.MustNotBeOverwritten)
        {
            Console.Error.WriteLine(
                $"Tript.App: '{fileName}' has a clip record that could not be read " +
                $"({existing.Failure}); the rename is refused rather than replacing it.");
            PushError(
                "The clip title could not be saved — this clip's record could not be read, and overwriting it would lose what else is on it.");
            return;
        }

        if (!_clipTitles.Save(fileName, title))
        {
            PushError("The clip title could not be saved — check the recording folder is writable.");
            return;
        }

        PushContent();
        }
    }

    internal void ToggleFavorite(ToggleFavoriteParameters? parameters)
    {
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.FilePath))
            return;

        var target = ResolveContentFile(parameters.FilePath);
        if (target is null)
            return;

        var fileName = Path.GetFileName(target);
        var relative = Path.GetRelativePath(EffectiveRoot, target).Replace(Path.DirectorySeparatorChar, '/');
        if (TopLevelDirectory(relative) is "clips" or "highlights")
        {
            if (!_clipTitles.SaveFavorite(fileName, parameters.Favorite))
            {
                PushError("The favorite could not be saved — check the recording folder is writable.");
                return;
            }
        }
        else
        {
            if (!_metadata.SaveFavorite(fileName, relative, parameters.Favorite))
            {
                PushError("The favorite could not be saved — check the recording folder is writable.");
                return;
            }
        }

        PushContent();
    }

    private string? ResolveContentFile(string fileName)
    {
        // Names are safe by construction here, but the same traversal discipline applies anyway.
        var candidate = _content.ResolveWithinRoot(fileName);
        if (candidate is null || !File.Exists(candidate))
            return null;
        return candidate;
    }

    // ---- bookmarks ----

    internal void AddBookmark(AddBookmarkParameters? parameters)
    {
        if (parameters is null || string.IsNullOrEmpty(parameters.FilePath))
            return;

        // TimeSpan.FromSeconds throws on NaN and overflows past ~9.22e11 — 1e18 is an unremarkable
        // JSON number — and the throw would land in IpcServer.Dispatch, so the user would see the
        // bookmark simply not appear. Refuse it here, where there is something to say about it.
        if (!IsUsableOffsetSeconds(parameters.Time))
        {
            PushError("That bookmark's time is not a real time, so it was not saved.");
            return;
        }

        var session = _sessionTracker.Active;
        if (session is not null)
        {
            // The recording is live: the bookmark goes into the session so the detector and the user share
            // one list.
            var bookmark = new Tript.Core.Bookmark
            {
                Type = ParseBookmarkType(parameters.Type),
                Time = TimeSpan.FromSeconds(parameters.Time),
            };
            session.AddBookmark(bookmark);
            return;
        }

        // A finished recording: the bookmark is appended to the video's metadata record. A video with no
        // record yet gets one.
        var target = ResolveContentFile(parameters.FilePath);
        if (target is null)
            return;

        // As in RenameContent, an unreadable record is preserved rather than replaced: a blank record with
        // one bookmark in it would cost the recording's game, title and every bookmark already on it.
        var fileName = Path.GetFileName(target);
        lock (_metadata.WriteGate)
        {
        var existing = _metadata.Read(fileName);
        if (existing.MustNotBeOverwritten)
        {
            Console.Error.WriteLine(
                $"Tript.App: '{fileName}' has a metadata record that could not be read " +
                $"({existing.Failure}); the bookmark is refused rather than replacing it.");
            PushError(
                "The bookmark could not be saved — this recording's metadata record could not be read, and overwriting it would lose its game and existing bookmarks.");
            return;
        }

        var metadata = existing.Record ?? new RecordingMetadata
        {
            VideoPath = Path.GetRelativePath(EffectiveRoot, target).Replace(Path.DirectorySeparatorChar, '/'),
        };
        metadata.Bookmarks.Add(new Tript.Core.Bookmark
        {
            // The store keys bookmarks by GUID, so an unparseable or absent id gets a fresh one.
            Id = Guid.TryParse(parameters.Id, out var parsedId) ? parsedId : Guid.NewGuid(),
            Type = ParseBookmarkType(parameters.Type),
            Time = TimeSpan.FromSeconds(parameters.Time),
        });

        if (!_metadata.Save(metadata))
        {
            // The frontend needs to know the add failed so it does not keep the bookmark in the UI.
            PushError("The bookmark could not be saved — check the recording folder is writable.");
        }
        }
    }

    internal void DeleteBookmark(DeleteBookmarkParameters? parameters)
    {
        if (parameters is null || string.IsNullOrEmpty(parameters.FilePath))
            return;

        var target = ResolveContentFile(parameters.FilePath);
        if (target is null)
            return;

        // Nothing to delete is silence; a record that could not be READ is an error the frontend must see.
        // Conflating the two made a delete look like it worked and the bookmark came back on the next list.
        var fileName = Path.GetFileName(target);
        lock (_metadata.WriteGate)
        {
        var existing = _metadata.Read(fileName);
        if (existing.MustNotBeOverwritten)
        {
            Console.Error.WriteLine(
                $"Tript.App: '{fileName}' has a metadata record that could not be read " +
                $"({existing.Failure}); the bookmark removal is refused rather than replacing it.");
            PushError(
                "The bookmark could not be removed — this recording's metadata record could not be read.");
            return;
        }

        var metadata = existing.Record;
        if (metadata is null)
            return;

        var id = Guid.TryParse(parameters.Id, out var parsedId) ? parsedId : Guid.Empty;
        metadata.Bookmarks.RemoveAll(b => b.Id == id);
        if (!_metadata.Save(metadata))
        {
            // A bookmark whose removal could not be persisted must not silently reappear on the next list.
            PushError("The bookmark could not be removed — check the recording folder is writable.");
        }
        }
    }

    private static Tript.Core.BookmarkType ParseBookmarkType(string type)
    {
        return Enum.TryParse<Tript.Core.BookmarkType>(type, ignoreCase: true, out var parsed)
            ? parsed
            : Tript.Core.BookmarkType.Manual;
    }

    // ---- recovery ----

    // Minimal orphan recovery: scan the recording root for files that exist on disk but have no entry
    // in the library list (a crashed recording leaves an .mp4.part, or an .mp4 with no metadata
    // record), and offer them via recoveryPrompt for the frontend to confirm or decline.
    internal void RaiseRecoveryPromptIfNeeded(ClientHandle client)
    {
        var orphans = FindOrphanFiles();
        if (orphans.Count == 0)
            return;

        var recoveryId = $"recovery-{DateTime.Now:yyyyMMddHHmmss}";
        client.Push("recoveryPrompt", JsonSerializer.SerializeToElement(new
        {
            recoveryId,
            files = orphans.Select(file => new
            {
                type = "recording",
                typeLabel = Path.GetFileName(file),
            }),
        }, Wire.Options));
        RequestNotification(NotificationKind.Recovery, "Unfinished recording found",
            $"Tript found {orphans.Count} recording file{(orphans.Count == 1 ? "" : "s")} to recover.");
    }

    private List<string> FindOrphanFiles()
    {
        var orphans = new List<string>();
        var root = new DirectoryInfo(EffectiveRoot);
        if (!root.Exists)
            return orphans;

        foreach (var file in root.EnumerateFiles("*.mp4", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(EffectiveRoot, file.FullName);
            var normalized = relative.Replace(Path.DirectorySeparatorChar, '/');

            // Orphaned means not part of the catalogue the library builds. A file in a sessions/ or
            // clips/ directory is never orphaned, in either legacy or game-scoped layout.
            if (IsTrashPath(normalized))
                continue;
            var contentDirectory = TopLevelDirectory(normalized);
            if (contentDirectory.Equals("sessions", StringComparison.Ordinal)
                || contentDirectory.Equals("clips", StringComparison.Ordinal)
                || contentDirectory.Equals("highlights", StringComparison.Ordinal))
                continue;

            orphans.Add(normalized);
        }

        return orphans;
    }

    internal void RecoveryConfirm(RecoveryConfirmParameters? parameters)
    {
        if (parameters is null)
            return;
        // "keep" is a no-op: the file already stays. Deleting an orphan is out of scope here.
        if (parameters.Action.Equals("delete", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    // ---- clipping ----

    internal void CreateAutomaticClips(CreateAutomaticClipsParameters? parameters)
    {
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.FilePath))
        {
            PushError("No recording was selected for automatic highlights.");
            return;
        }

        var sourcePath = ContentServer.ResolveWithinRoot(EffectiveRoot, parameters.FilePath);
        if (sourcePath is null || !File.Exists(sourcePath)
            || !TopLevelDirectory(parameters.FilePath.Replace('\\', '/'))
                .Equals("sessions", StringComparison.Ordinal))
        {
            PushError("That recording is not inside the sessions library.");
            return;
        }

        var metadata = _metadata.Load(Path.GetFileName(sourcePath));
        if (metadata is null)
        {
            PushError("That recording has no event metadata, so no automatic highlights can be created.");
            return;
        }

        var candidates = metadata.Bookmarks
            .Where(bookmark => bookmark.IsAutomaticClipCandidate == true
                || (bookmark.IsAutomaticClipCandidate is null
                    && bookmark.Type.IsIncludedInHighlights()))
            .ToList();
        if (candidates.Count == 0)
        {
            PushError("That recording has no positive events to turn into highlights.");
            return;
        }

        var sourceSessionPath = Path.GetRelativePath(EffectiveRoot, sourcePath)
            .Replace(Path.DirectorySeparatorChar, '/');
        if (!QueueAutomaticClips(sourcePath, sourceSessionPath, candidates))
            PushError("Automatic highlights are already being created for another recording.");
    }

    internal void ToggleAutomaticClipPause()
    {
        lock (_automaticClipGate)
        {
            if (_automaticClipJob is null)
                return;
            if (_backgroundWorkSuspendedForRecording)
                return;

            _automaticClipJob.PausedByUser = !_automaticClipJob.PausedByUser;
            Monitor.PulseAll(_automaticClipGate);
        }

        PushState(IsRecording, CurrentGameId);
        PushContent();
    }

    private bool QueueAutomaticClips(string sourcePath, string sourceSessionPath,
        IReadOnlyList<Bookmark> bookmarks)
    {
        var regions = AutomaticClipPlanner.Plan(bookmarks.Select(bookmark => bookmark.Time));
        if (regions.Count == 0)
            return false;

        var job = new AutomaticClipJob
        {
            SourceSessionPath = sourceSessionPath,
            Total = regions.Count,
        };
        lock (_automaticClipGate)
        {
            if (_automaticClipJob is not null)
                return false;
            _automaticClipJob = job;
        }

        PushState(IsRecording, CurrentGameId);
        PushContent();

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                _clipEngine ??= BuildClipEngine();
                var sourceBaseName = Path.GetFileNameWithoutExtension(sourcePath);
                var outputDirectory = HighlightsDirectoryForSource(sourcePath);
                var failures = 0;

                for (var index = 0; index < regions.Count; index++)
                {
                    try
                    {
                        lock (_automaticClipGate)
                        {
                            while (ReferenceEquals(_automaticClipJob, job)
                                && (job.PausedByUser || _backgroundWorkSuspendedForRecording))
                                Monitor.Wait(_automaticClipGate);
                        }

                        if (!ReferenceEquals(_automaticClipJob, job))
                            return;

                        var region = regions[index];
                        var outputPath = Path.Combine(outputDirectory,
                            $"{sourceBaseName}-highlight-{index + 1}-{Guid.NewGuid():N}.mp4");
                        var results = _clipEngine.CreateClips(new ClipRequest
                        {
                            OperationId = $"automatic-{Guid.NewGuid():N}",
                            SourcePath = sourcePath,
                            Regions = [region],
                            Mode = ClipMode.Combine,
                            OutputPath = outputPath,
                            EncoderFamily = "libx264",
                            PreferStreamCopy = true,
                        });

                        foreach (var result in results)
                        {
                            _clipTitles.SaveAutomatic(Path.GetFileName(result), sourceSessionPath,
                                region.Start.TotalSeconds, region.End.TotalSeconds);
                        }
                        AttachGameToClips(results, sourceSessionPath);

                        lock (_automaticClipGate)
                            job.Completed++;
                        PushState(IsRecording, CurrentGameId);
                    }
                    catch (Exception exception)
                    {
                        failures++;
                        Log.Error(exception, "AppHost: automatic highlight {Index} failed for {SourcePath}",
                            index + 1, sourcePath);
                    }
                }

                if (failures > 0)
                    PushError($"{failures} automatic highlight{(failures == 1 ? "" : "s")} could not be created.");
            }
            catch (Exception exception)
            {
                Log.Error(exception, "AppHost: automatic highlight creation failed for {SourcePath}", sourcePath);
                PushError($"Automatic highlights could not be created: {exception.Message}");
            }
            finally
            {
                lock (_automaticClipGate)
                {
                    if (ReferenceEquals(_automaticClipJob, job))
                        _automaticClipJob = null;
                    Monitor.PulseAll(_automaticClipGate);
                }

                PushState(IsRecording, CurrentGameId);
                PushContent();
            }
        });
        return true;
    }

    // Reports a clip that could not even be started — a source path that does not resolve inside the
    // recording root. It reuses the importProgress "error" frame the engine's own failures use, which
    // is what the clip dialog renders its failure state from.
    internal void PushClipError(string operationId, string message)
    {
        _ipc.Broadcast("importProgress", JsonSerializer.SerializeToElement(new
        {
            id = operationId,
            status = "error",
            error = message,
        }, Wire.Options));
    }

    internal void CreateClip(ClipRequest request)
    {
        _clipEngine ??= BuildClipEngine();

        // Never block synchronously: the ffmpeg run is off the IPC thread and progress arrives as
        // importProgress messages.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                _ipc.Broadcast("importProgress", JsonSerializer.SerializeToElement(new
                {
                    id = request.OperationId,
                    status = "importing",
                }, Wire.Options));

                var results = _clipEngine.CreateClips(request);

                // Persisted against every produced file (one in combine mode, one per region in separate mode).
                // A failed write is logged inside the store and does not fail the clip.
                if (!string.IsNullOrWhiteSpace(request.Title))
                {
                    foreach (var result in results)
                        _clipTitles.Save(Path.GetFileName(result), request.Title);
                }
                if (!string.IsNullOrWhiteSpace(request.SourceSessionPath))
                {
                    foreach (var result in results)
                        _clipTitles.SaveSourceSession(Path.GetFileName(result), request.SourceSessionPath);
                    AttachGameToClips(results, request.SourceSessionPath);
                }

                _ipc.Broadcast("importProgress", JsonSerializer.SerializeToElement(new
                {
                    id = request.OperationId,
                    status = "done",
                    content = new ContentItem
                    {
                        ContentType = "clip",
                        FileName = Path.GetFileName(results[0]),
                        FilePath = Path.GetRelativePath(EffectiveRoot, results[0]).Replace(Path.DirectorySeparatorChar, '/'),
                        Title = string.IsNullOrWhiteSpace(request.Title) ? null : request.Title,
                    },
                }, Wire.Options));

                // A clip completed: the catalogue changed.
                PushContent();
            }
            catch (Exception exception)
            {
                _ipc.Broadcast("importProgress", JsonSerializer.SerializeToElement(new
                {
                    id = request.OperationId,
                    status = "error",
                    error = exception.Message,
                }, Wire.Options));
            }
        });
    }

    internal void ConvertToSdr(ConvertToSdrParameters? parameters)
    {
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.FilePath))
            return;

        var operationId = string.IsNullOrWhiteSpace(parameters.Id)
            ? $"sdr-{Guid.NewGuid():N}"
            : parameters.Id.Trim();
        if (BackgroundWorkSuspendedForRecording)
        {
            PushConversionProgress(operationId, "error", "SDR conversion is unavailable while recording.");
            return;
        }

        var source = ContentServer.ResolveWithinRoot(EffectiveRoot, parameters.FilePath);
        var relative = source is null ? null
            : Path.GetRelativePath(EffectiveRoot, source).Replace(Path.DirectorySeparatorChar, '/');
        var topLevel = relative is null ? string.Empty : TopLevelDirectory(relative);
        if (source is null || !File.Exists(source) || topLevel is not ("clips" or "highlights"))
        {
            PushConversionProgress(operationId, "error", "That file is not a clip or highlight inside the recording folder.");
            return;
        }

        MediaProbe? probe;
        try
        {
            probe = LibraryProbe;
            if (probe is null)
                throw new InvalidOperationException("Media tools are unavailable.");
            var info = probe.Probe(source);
            if (!info.IsHdr)
                throw new InvalidOperationException("The selected file is already SDR.");
            if (!double.IsFinite(info.DurationSeconds) || info.DurationSeconds <= 0)
                throw new InvalidOperationException("The selected file has no usable duration.");

            string output;
            lock (_sdrConversionGate)
            {
                if (!_sdrConversions.Add(source))
                {
                    PushConversionProgress(operationId, "error", "An SDR conversion is already running for this file.");
                    return;
                }
                output = NextSdrPath(source);
                _reservedClipOutputs.Add(output);
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    PushConversionProgress(operationId, "importing", null);
                    _clipEngine ??= BuildClipEngine();
                    var results = _clipEngine.CreateClips(new ClipRequest
                    {
                        OperationId = operationId,
                        SourcePath = source,
                        Regions = [ClipRegion.FromSeconds(0, info.DurationSeconds)],
                        Mode = ClipMode.Combine,
                        OutputPath = output,
                        EncoderFamily = "libx264",
                        ForceSdr = true,
                    });
                    var converted = results[0];
                    if (!_clipTitles.SaveConvertedFrom(Path.GetFileName(source), Path.GetFileName(converted)))
                        throw new InvalidOperationException("The SDR file was created, but its metadata could not be saved.");

                    PushConversionProgress(operationId, "done", new ContentItem
                    {
                        ContentType = parameters.ContentType,
                        FileName = Path.GetFileName(converted),
                        FilePath = Path.GetRelativePath(EffectiveRoot, converted).Replace(Path.DirectorySeparatorChar, '/'),
                        IsHdr = false,
                    });
                    PushContent();
                }
                catch (Exception exception)
                {
                    PushConversionProgress(operationId, "error", exception.Message);
                    try { if (File.Exists(output)) File.Delete(output); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                finally
                {
                    lock (_sdrConversionGate)
                    {
                        _sdrConversions.Remove(source);
                        _reservedClipOutputs.Remove(output);
                    }
                }
            });
        }
        catch (Exception exception)
        {
            PushConversionProgress(operationId, "error", exception.Message);
        }
    }

    private void PushConversionProgress(string id, string status, object? content)
    {
        if (status == "error")
        {
            PushClipError(id, content?.ToString() ?? "SDR conversion failed.");
            return;
        }

        _ipc.Broadcast("importProgress", JsonSerializer.SerializeToElement(new { id, status, content }, Wire.Options));
    }

    private string NextSdrPath(string source)
    {
        var directory = Path.GetDirectoryName(source)!;
        var stem = Path.GetFileNameWithoutExtension(source) + "-sdr";
        var candidate = Path.Combine(directory, stem + ".mp4");
        for (var suffix = 2; File.Exists(candidate) || _reservedClipOutputs.Contains(candidate); suffix++)
            candidate = Path.Combine(directory, $"{stem}-{suffix}.mp4");
        return candidate;
    }

    internal bool BackgroundWorkSuspendedForRecording
    {
        get
        {
            lock (_automaticClipGate)
                return _backgroundWorkSuspendedForRecording;
        }
    }

    private void SetBackgroundWorkSuspendedForRecording(bool suspended)
    {
        lock (_automaticClipGate)
        {
            _backgroundWorkSuspendedForRecording = suspended;
            Monitor.PulseAll(_automaticClipGate);
        }
    }

    private IClipEngine BuildClipEngine()
    {
        var (ffmpeg, ffprobe) = new FfmpegLocator().Locate();
        return new ClipEngine(ffmpeg, new MediaProbe(ffprobe));
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
        return ProcessNameGameDetector.NormalizeProcessName(entry is null ? gameId : ExecutableOf(entry));
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
        Directory.CreateDirectory(directory);
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
