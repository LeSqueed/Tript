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
using Tript.App.Resolver;
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
    private readonly ResolverClient? _resolverClient;
    private readonly bool _ownsResolverClient;
    private readonly Timer? _modelCheckTimer;
    private readonly object _audioLevelGate = new();
    private Timer? _audioLevelTimer;
    private readonly ObsAudioLevelMonitor? _audioLevelMonitor;
    private readonly AudioDeviceInventory _audioDeviceInventory;
    private readonly object _audioDeviceRefreshGate = new();
    private Timer? _audioDeviceTimer;

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
            Log.Warning("no thumbnails or durations in the library — {Reason}", exception.Message);
            return null;
        }
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    private MediaProbe? _libraryProbe;

    // Files whose duration could not be read, so a broken file is probed at most once per process.
    private readonly HashSet<string> _unprobeable = new(StringComparer.Ordinal);

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
    private DetectionHost? _detectionHost;
    private RecordingMetadata? _pendingMetadata;
    private string? _activeOutputPath;
    private string? _activeSessionPath;
    private PendingSessionReassignment? _pendingSessionReassignment;
    private RecordingMode? _activeRecordingMode;
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
    // The effective automatic-clip/live-highlight gate decided when this session started. Kept
    // separate from _liveHighlightsEnabled: that flag is already cleared by the time the recording
    // is finalized, so the post-stop clip cut must read the value the session began with.
    private bool _liveHighlightsEnabledAtSessionStart;
    private CancellationTokenSource? _captureWaitCancellation;
    private int _recordingStopRequested;
    private readonly DetectedGameTracker _detectedGames = new();

    // Replaced wholesale under _gameListGate, never mutated in place: GameList is read from the
    // IPC pool, the detector's timer and the hook probe, and a reader holding the old list must
    // be able to finish enumerating it.
    private readonly object _gameListGate = new();
    private List<GameInfo> _catalogueGames = [];
    private IClipEngine? _clipEngine;
    private readonly object _clipQueueGate = new();
    private readonly Queue<ClipRequest> _clipQueue = [];
    private bool _clipQueueActive;
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

    private sealed record PendingSessionReassignment(
        string? OriginalGame,
        string? OriginalGameId,
        string TargetGame,
        string TargetGameId);

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
        TimeSpan? recorderStopTimeout = null, bool enableModelDelivery = false,
        ResolverClient? resolverClient = null,
        AudioDeviceInventory? audioDeviceInventory = null)
    {
        _options = options;
        _settingsStore = settingsStore;
        _runtime = runtime;
        _audioLevelMonitor = runtime is null ? null : new ObsAudioLevelMonitor();
        _audioDeviceInventory = audioDeviceInventory ?? new AudioDeviceInventory();
        _sessionTracker = sessionTracker;
        _primaryDisplay = primaryDisplay;
        _resolverClient = resolverClient;
        _gameCatalog = GameCatalog.Load(Path.Combine(AppContext.BaseDirectory, "data", "games.json"));
#if TRIPT_TRAINING
        MigrateLegacyTrainingFolders(_gameCatalog, TrainingPaths.RootPath,
            TrainingPaths.InstalledModelsPath);
#endif
        _discovery = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)
            ? GameDiscoveryService.CreateDefault(new WindowsXboxPackageProvider())
            : null;
        _recorderStopTimeout = recorderStopTimeout ?? TimeSpan.FromSeconds(10);

        if (_resolverClient is null)
        {
            var resolverConfig = ResolverConfig.FromFile();
            if (resolverConfig is not null)
            {
                _resolverClient = new ResolverClient(resolverConfig,
                    registry: new ResolverGameRegistry());
                _ownsResolverClient = true;
            }
        }

        if (enableModelDelivery)
        {
            MigrateModelFolders();
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
                manifestUri: _resolverClient?.ManifestUri,
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
        if (_modelManager is not null)
            _modelCheckTimer = new Timer(_ => EnsureModelsForGameList(), null, TimeSpan.FromHours(24),
                TimeSpan.FromHours(24));
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
    private string? _activeDetectionGameId;
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
    // turns the content server's /api/content/<path> into a reader for the whole machine, for any
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
        => FilePaths.IsAtOrUnder(sensitive, candidate);

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
            _audioDeviceInventory.Refresh();
            _ipc.Start();
            _content.Start();
            _ui.Start();
            if (_audioLevelMonitor is not null)
                _audioLevelTimer = new Timer(_ => PushAudioLevels(), null, TimeSpan.Zero,
                    TimeSpan.FromMilliseconds(250));
            _audioDeviceTimer = new Timer(_ => RefreshAudioDevices(), null, TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5));

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
        _modelCheckTimer?.Dispose();
        _audioLevelTimer?.Dispose();
        lock (_audioLevelGate)
        {
        }
        _audioDeviceTimer?.Dispose();
        lock (_audioDeviceRefreshGate)
        {
        }
        _audioLevelMonitor?.Dispose();
        _detectionHost?.Dispose();
        _detector?.Dispose();
        _fullscreenDetector?.Dispose();

        if (_modelManager is not null)
        {
            _modelManager.StatusChanged -= OnModelStatusChanged;
            _modelManager.Dispose();
        }
        if (_ownsResolverClient)
            _resolverClient?.Dispose();

        // Preserve recording metadata when possible, but never let a dead recorder block shutdown.
        try
        {
            StopRecording();
        }
        catch (Exception exception)
        {
            Log.Warning("recording cleanup failed during shutdown: {Reason}", exception.Message);
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
        _thumbnails.Dispose();
        _ui.Dispose();
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

    private void MigrateModelFolders()
    {
        var modelsRoot = GameModelPaths.ModelsRoot;
        if (!Directory.Exists(modelsRoot))
            return;

        foreach (var entry in _gameCatalog.Entries)
        {
            foreach (var legacy in entry.LegacyGameIds ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(legacy))
                    continue;

                var from = Path.Combine(modelsRoot, GameModelPaths.ValidateGameId(legacy));
                var to = Path.Combine(modelsRoot, GameModelPaths.ValidateGameId(entry.GameId));
                if (Directory.Exists(from) && !Directory.Exists(to))
                {
                    try
                    {
                        Directory.Move(from, to);
                        var installedPath = Path.Combine(to, "installed.json");
                        if (File.Exists(installedPath)
                            && JsonNode.Parse(File.ReadAllText(installedPath)) is JsonObject installed)
                        {
                            installed["gameId"] = entry.GameId;
                            var temporaryPath = installedPath + ".tmp";
                            File.WriteAllText(temporaryPath, installed.ToJsonString(Wire.Options));
                            File.Move(temporaryPath, installedPath, true);
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                        or JsonException)
                    {
                        Log.Warning("could not migrate model folder {From} to {To}: {Reason}",
                            from, to, exception.Message);
                    }
                }
            }
        }
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
            Log.Warning("the training folder picker failed: {Reason}", exception.Message);
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
            Log.Warning("the folder picker failed: {Reason}", exception.Message);
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
                Log.Warning("the game executable picker failed: {Reason}", exception.Message);
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

        if (OperatingSystem.IsWindows() && !ExecutableNames.HasExeExtension(normalized))
            return null;

        return normalized;
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
                if (PatchUpdatesAutomaticClipWindows(patch.Value)
                    && !ValidateAutomaticClipWindows(candidate, out var windowError))
                    return windowError;

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

    private static bool PatchUpdatesAutomaticClipWindows(JsonElement patch) =>
        patch.ValueKind == JsonValueKind.Object
        && ((patch.TryGetProperty("recording", out var recording)
            && recording.ValueKind == JsonValueKind.Object
            && (recording.TryGetProperty("automaticClipBeforeSeconds", out _)
                || recording.TryGetProperty("automaticClipAfterSeconds", out _)))
            || PatchUpdatesGameList(patch));

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
                activeRecordingMode = recording ? _activeRecordingMode?.ToString() : null,
                game,
                activeModelGameId = _activeDetectionGameId,
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

    private void RefreshAudioDevices()
    {
        lock (_audioDeviceRefreshGate)
        {
            if (_disposed || !_audioDeviceInventory.Refresh())
                return;
            PushSettings();
        }
    }

    private void PushAudioLevels()
    {
        lock (_audioLevelGate)
        {
            if (_disposed)
                return;

            try
            {
                if (_audioLevelMonitor is null)
                    return;

                var sources = _settingsStore.Load().Audio.Tracks
                    .SelectMany(track => track.Sources)
                    .Where(source => !string.IsNullOrWhiteSpace(source.DeviceId))
                    .Select(source => new AudioLevelSource(source.Kind, source.DeviceId!))
                    .Distinct()
                    .ToList();
                var levels = _audioLevelMonitor.Read(sources);
                _ipc.Broadcast("audioLevels", JsonSerializer.SerializeToElement(new
                {
                    levels = levels.Select(level => new { deviceId = level.Key, peak = level.Value }).ToList(),
                }, Wire.Options));
            }
            catch (Exception)
            {
            }
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
            audioNode["devices"] = JsonSerializer.SerializeToNode(_audioDeviceInventory.Snapshot,
                SettingsSerialization.Options);
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

        DeleteItems([parameters], permanent: false);
        PushContent();
        PushTrash();
    }

    internal void DeleteMultipleContent(DeleteMultipleContentParameters? parameters)
    {
        if (parameters?.Items is null)
            return;

        DeleteItems(parameters.Items, parameters.Permanent);

        PushContent();
        PushTrash();
    }

    private void DeleteItems(IReadOnlyList<DeleteContentParameters> items, bool permanent)
    {
        var processed = new HashSet<string>(ContentPathComparer);
        var clipRecords = items.Any(item => item.DeleteLinkedHighlights)
            ? _clipTitles.EnumerateRecords()
            : [];

        foreach (var item in items)
        {
            if (!string.IsNullOrEmpty(item.FileName))
                DeleteOne(item, permanent || item.Permanent, clipRecords, processed);
        }
    }

    // One item, without the pushes: single, batch, and cascaded deletes share this path.
    private void DeleteOne(DeleteContentParameters item, bool permanent,
        IReadOnlyList<(string ClipFileName, ClipTitleRecord Record)> clipRecords,
        HashSet<string> processed, ClipTitleRecord? enumeratedClipRecord = null)
    {
        // Resolve against the root even when the file is missing: a delete for a video whose file was
        // removed out-of-band must still drop the metadata record. ResolveContentFile refuses paths with
        // no file on disk, so use the traversal-safe resolver directly.
        var target = _content.ResolveWithinRoot(item.FileName);
        if (target is null || !processed.Add(target))
            return;

        var fileName = Path.GetFileName(target);
        var relative = RelativeToRoot(target);
        var contentType = ResolveContentType(item.ContentType, relative);
        if (item.DeleteLinkedHighlights && contentType == "recording"
            && TopLevelDirectory(relative) is not ("clips" or "highlights"))
        {
            DeleteLinkedAutomaticHighlights(target, relative, permanent, clipRecords, processed);
        }

        if (permanent)
        {
            UnlinkContent(target, fileName);
            return;
        }

        // The records are read before they move, so the entry can be listed with the title, game and
        // length the library was showing.
        var seed = new TrashEntryRecord
        {
            ContentType = contentType,
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

        var clipRecord = enumeratedClipRecord ?? _clipTitles.LoadRecord(fileName);
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
        // Close the extraction check-to-publish race before the cache is snapshotted for the trash
        // transaction. A worker already decoding this source must not recreate the thumbnail after
        // the source and its existing cache entry have moved.
        _thumbnails.Invalidate(fileName);
        AddIfPresent(files, _thumbnails.PathFor(fileName));

        if (files.Count == 0)
            return;

        if (_trash.Add(files, seed, out var failure) is null)
        {
            Log.Warning("could not move {Target} to the trash: {Failure}", target, failure);
            PushError($"'{fileName}' could not be moved to the trash ({failure}), so it was left where it is.");
        }
        else
        {
            // A request can arrive after the pre-move invalidation and enqueue against the source
            // before TrashStore moves it. Invalidate once more after the move and remove anything
            // that worker managed to publish after the file snapshot was taken.
            _thumbnails.Delete(fileName);
        }
    }

    private void DeleteLinkedAutomaticHighlights(string sourceTarget, string sourcePath, bool permanent,
        IReadOnlyList<(string ClipFileName, ClipTitleRecord Record)> clipRecords,
        HashSet<string> processed)
    {
        var highlightsDirectory = HighlightsDirectoryPathForSource(sourceTarget);
        foreach (var (clipFileName, record) in clipRecords)
        {
            if (!record.IsAutomatic || record.Favorite || string.IsNullOrWhiteSpace(record.SourceSessionPath))
                continue;
            if (!string.Equals(NormalizeSourcePath(record.SourceSessionPath), sourcePath,
                ContentPathComparison))
                continue;

            DeleteOne(new DeleteContentParameters
            {
                ContentType = "highlight",
                FileName = RelativeToRoot(Path.Combine(highlightsDirectory, clipFileName)),
            }, permanent, clipRecords, processed, record);
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
            Log.Warning("could not delete {Target}: {Reason}", target, exception.Message);
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
            Log.Warning("{FileName} has a metadata record that could not be read ({Failure}); the bookmark is refused rather than replacing it.", fileName, existing.Failure);
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
            Log.Warning("{FileName} has a metadata record that could not be read ({Failure}); the bookmark removal is refused rather than replacing it.", fileName, existing.Failure);
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

}
