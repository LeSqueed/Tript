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
using Tript.App.Updater;
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
    UpdateReady,
}

internal sealed partial class AppHost : IDisposable
{
    private readonly AppOptions _options;
    private readonly SettingsStore _settingsStore;
    private readonly object _settingsUpdateGate = new();
    private readonly ObsRuntime? _runtime;
    private readonly RecordingSessionTracker _sessionTracker;

    private readonly DisplaySize? _primaryDisplay;

    private readonly SessionToken _token = new();

    private readonly AppController _controller;
    private readonly IpcServer _ipc;
    private readonly ContentServer _content;
    private readonly UiHost _ui;
    private readonly RecordingMetadataStore _metadata;
    private readonly RecordingBookmarks _bookmarks;
    private readonly ClipTitleStore _clipTitles;
    private readonly ThumbnailStore _thumbnails;
    private readonly TrashStore _trash;
    private readonly GameCatalog _gameCatalog;
    private readonly GameIdAliasStore _gameIdAliases;
    private readonly GameModelManager? _modelManager;
    private readonly ResolverClient? _resolverClient;
    private readonly bool _ownsResolverClient;
    private readonly Timer? _modelCheckTimer;
    private readonly AudioLevelFeed _audioLevels;
    private int _windowVisible = 1;

    private readonly Lazy<(string Ffmpeg, string Ffprobe)?> _libraryTools = new(() =>
    {
        try
        {
            return new FfmpegLocator().Locate();
        }
        catch (FfmpegNotFoundException exception)
        {
            Log.Warning("no thumbnails or durations in the library: {Reason}", exception.Message);
            return null;
        }
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly LibraryProbe _libraryProbe;
    private readonly CoalescingRunner _contentPush;

    private readonly object _recorderGate = new();
    private readonly TimeSpan _recorderStopTimeout;
    private bool _shuttingDown;

    private RecorderStateMachine? _recorder;
    private IRecorderSession? _recorderSession;
    private ObsSource? _colourSource;
    private bool _stopFinalizationPending;
    private ProcessNameGameDetector? _detector;
    private FullscreenGameDetector? _fullscreenDetector;
    private readonly GameInventoryScanner _gameInventory;
    private readonly CancellationTokenSource _discoveryCancellation = new();
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

    private bool _liveHighlightsEnabledAtSessionStart;
    private CancellationTokenSource? _captureWaitCancellation;
    private int _recordingStopRequested;
    private readonly DetectedGameTracker _detectedGames = new();

    private readonly object _gameListGate = new();
    private List<GameInfo> _catalogueGames = [];
    private LibraryGames? _libraryGames;
    private IClipEngine? _clipEngine;
    private readonly SerialWorkQueue<ClipRequest> _clipQueue;
    private readonly SdrOutputReservations _sdrOutputs = new();

    private static readonly TimeSpan TrashPurgeInterval = TimeSpan.FromHours(1);

    private Timer? _trashPurgeTimer;

    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);

    private readonly UpdateManager? _updateManager;
    private Timer? _updateCheckTimer;
    private string? _lastNotifiedUpdateStage;

    private bool _disposed;

    internal AppHost(AppOptions options, SettingsStore settingsStore, ObsRuntime? runtime,
        RecordingSessionTracker sessionTracker, DisplaySize? primaryDisplay = null,
        TimeSpan? recorderStopTimeout = null, bool enableModelDelivery = false,
        ResolverClient? resolverClient = null,
        AudioDeviceInventory? audioDeviceInventory = null,
        GameIdAliasStore? gameIdAliases = null)
    {
        _contentPush = new CoalescingRunner(BroadcastContent, ReportContentFailure);
        _clipQueue = new SerialWorkQueue<ClipRequest>(ProcessClip, ReportClipFailure);
        _options = options;
        _settingsStore = settingsStore;
        _runtime = runtime;
        _audioLevels = new AudioLevelFeed(settingsStore,
            runtime is null ? null : new ObsAudioLevelMonitor(),
            audioDeviceInventory ?? new AudioDeviceInventory(),
            BroadcastAudioLevels,
            PushSettings);
        _sessionTracker = sessionTracker;
        _primaryDisplay = primaryDisplay;
        _resolverClient = resolverClient;
        _gameIdAliases = gameIdAliases ?? new GameIdAliasStore();
        _libraryProbe = new LibraryProbe(() => _libraryTools.Value?.Ffprobe);
        _gameCatalog = GameCatalog.Load(Path.Combine(AppContext.BaseDirectory, "data", "games.json"));
#if TRIPT_TRAINING
        MigrateLegacyTrainingFolders(_gameCatalog, TrainingPaths.RootPath,
            TrainingPaths.InstalledModelsPath);
#endif
        _gameInventory = new GameInventoryScanner(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)
            ? GameDiscoveryService.CreateDefault(new WindowsXboxPackageProvider())
            : null);
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
                return File.Exists(Path.Combine(gamePath, "events.json")) &&
                    (File.Exists(Path.Combine(gamePath, "model.onnx")) ||
                     File.Exists(Path.Combine(gamePath, "ocr_model.onnx")));
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

        EffectiveRoot = RecordingRootPolicy.Resolve(options, settingsStore.Load());

        _controller = new AppController(this);
        _ipc = new IpcServer(_controller, _token, options.ControlPort, options.UiPort);
        _metadata = new RecordingMetadataStore(ContentLayout.MetadataRoot(EffectiveRoot));
        _bookmarks = new RecordingBookmarks(_metadata, RelativeToRoot, PushError);
        _clipTitles = new ClipTitleStore(ContentLayout.MetadataRoot(EffectiveRoot));
        _thumbnails = new ThumbnailStore(ContentLayout.ThumbnailRoot(EffectiveRoot), CreateThumbnailExtractor);
        _trash = new TrashStore(ContentLayout.TrashRoot(EffectiveRoot));
        _content = new ContentServer(EffectiveRoot, _token, _thumbnails, options.ContentPort);
        _ui = new UiHost(options.WebRoot, _token, options.UiPort);

        _updateManager = options.DisableUpdater
            ? null
            : new UpdateManager(UpdateStagingPaths.InstallRootFromAppBaseDirectory(AppContext.BaseDirectory),
                UpdateManager.CurrentInstalledVersion() ?? "0.0.0");
        if (_updateManager is not null)
        {
            _updateManager.SweepLeftovers();
            _updateManager.StatusChanged += OnUpdateStatusChanged;
        }

        Directory.CreateDirectory(EffectiveRoot);
        ReloadGameList();
        if (_modelManager is not null)
        {
            _modelCheckTimer = new Timer(_ =>
            {
                EnsureModelsForGameList();
                _ = ReconcileCustomGameIdentitiesAsync();
            }, null, TimeSpan.FromHours(24), TimeSpan.FromHours(24));
        }
    }

    internal AppOptions Options => _options;

    internal SettingsStore SettingsStore => _settingsStore;

    internal event Action<SettingsModel>? SettingsChanged;

    internal event Action<bool, string?>? StateChanged;

    internal event Action<NotificationKind, string, string>? NotificationRequested;

    internal event Action? RestartForUpdateRequested;

    internal ObsRuntime? Runtime => _runtime;

    internal IpcServer Ipc => _ipc;

    internal ContentServer Content => _content;

    internal string UiUrl => _token.BuildUiUrl(_options.UiPort);

    internal bool IsRecording => _recorder is not null && _recorder.Snapshot.State != RecorderState.Idle;

    internal string? CurrentGameId => _recorder is not null && _recorder.Snapshot.State != RecorderState.Idle
        ? _currentGameId
        : null;

    private string? _currentGameId;
    private string? _activeDetectionGameId;
    private string? _recordingProcessOwner;

    internal string EffectiveRoot { get; private set; }

    internal bool ConvertHdrClipsToSdr => _settingsStore.Load().General.ConvertHdrClipsToSdr;

    private IThumbnailExtractor? CreateThumbnailExtractor()
    {
        var tools = _libraryTools.Value;
        return tools is null ? null : new FfmpegThumbnailExtractor(tools.Value.Ffmpeg, tools.Value.Ffprobe);
    }

    public void Run()
    {
        using var shutdownRequested = new ManualResetEventSlim(false);
        Action onShutdown = shutdownRequested.Set;
        _ipc.ShutdownRequested += onShutdown;

        try
        {
            _audioLevels.LoadDevices();
            _ipc.Start();
            _content.Start();
            _ui.Start();
            _audioLevels.Start();

            PurgeExpiredTrash();
            _trashPurgeTimer = new Timer(_ => PurgeExpiredTrash(), null, TrashPurgeInterval, TrashPurgeInterval);

            if (_updateManager is not null)
            {
                if (_settingsStore.Load().General.CheckForUpdatesAutomatically)
                    _ = CheckForUpdatesAutomaticAsync();
                _updateCheckTimer = new Timer(_ => { _ = CheckForUpdatesAutomaticAsync(); }, null,
                    UpdateCheckInterval, UpdateCheckInterval);
            }

            WireAutoStart();

            Console.WriteLine($"READY {UiUrl}");
            Console.Out.Flush();
            StartDiscoveryScan();

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
        shutdownRequested.Wait();
        Console.WriteLine("SHUTDOWN");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _audioLevels.Dispose();

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
            _gameInventory.CurrentScan.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "AppHost: launcher game discovery did not settle cleanly during shutdown.");
        }

        _trashPurgeTimer?.Dispose();
        _updateCheckTimer?.Dispose();
        if (_updateManager is not null)
        {
            _updateManager.StatusChanged -= OnUpdateStatusChanged;
            _updateManager.Dispose();
        }
        _modelCheckTimer?.Dispose();
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

    internal Func<string?>? FolderPicker { get; set; }

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

        var previousGames = GameListFingerprint.Of(_settingsStore.Load().Game.GameList);
        SettingsModel settings;
        string? failure;
        bool saved;
        try
        {
            saved = _settingsStore.TryUpdate(candidate =>
            {
                SettingsPatch.Apply(candidate, patch.Value);
                if (!ValidateGameList(candidate.Game.GameList, out var validationError,
                    requireExistingExecutables: SettingsPatch.UpdatesGameList(patch.Value)))
                    return validationError;
                if (SettingsPatch.UpdatesAutomaticClipWindows(patch.Value)
                    && !ValidateAutomaticClipWindows(candidate, out var windowError))
                    return windowError;
                if (!ValidateHotkeys(candidate, out var hotkeyError))
                    return hotkeyError;

                string effectiveRoot;
                try
                {
                    effectiveRoot = RecordingRootPolicy.Resolve(_options, candidate);
                }
                catch (Exception exception) when (exception is ArgumentException or IOException
                    or NotSupportedException or PathTooLongException)
                {
                    return "the recording directory is not a usable path.";
                }

                return RecordingRootPolicy.Prepare(effectiveRoot);
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
        var currentGames = GameListFingerprint.Of(settings.Game.GameList);
        var gameKeysChanged = !previousGames.Keys.SequenceEqual(currentGames.Keys);
        var gameNamesChanged = !previousGames.Names.SequenceEqual(currentGames.Names);
        if (gameKeysChanged || gameNamesChanged)
        {
            PushGameList();
            if (gameKeysChanged)
                RebuildDetectionTargets();
        }
        if (gameNamesChanged)
            PushContent();

        var effectiveRoot = RecordingRootPolicy.Resolve(_options, settings);
        if (!string.Equals(effectiveRoot, EffectiveRoot, StringComparison.Ordinal))
        {
            EffectiveRoot = effectiveRoot;
            _content.UpdateRoot(effectiveRoot);
            _metadata.UpdateRoot(ContentLayout.MetadataRoot(effectiveRoot));
            _clipTitles.UpdateRoot(ContentLayout.MetadataRoot(effectiveRoot));
            _thumbnails.UpdateRoot(ContentLayout.ThumbnailRoot(effectiveRoot));
            _trash.UpdateRoot(ContentLayout.TrashRoot(effectiveRoot));
        }

        SettingsChanged?.Invoke(settings);
        PushSettings();
        PushSettingsUpdateResult(requestId, true, null);
        return true;
    }

    private sealed record GameListFingerprint(
        List<(string Id, string? Executable, string? ExecutablePath)> Keys,
        List<(string Id, string Name)> Names)
    {
        internal static GameListFingerprint Of(IEnumerable<GameSetting> games)
        {
            var list = games.ToList();
            return new GameListFingerprint(
                list.Select(game => (game.Id, game.Executable, game.ExecutablePath)).ToList(),
                list.Select(game => (game.Id, game.Name)).ToList());
        }
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

    internal void PushState(bool recording, string? gameId)
    {
        var representedGameId = gameId ?? (!recording ? CurrentDetectedGameId() : null);
        var game = representedGameId is null ? null : new GameInfo
        {
            Id = representedGameId,
            Name = GameDisplayName(representedGameId),
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

                startedAt = recording ? ContentCatalogue.UnixSeconds(_pendingMetadata?.StartTime ?? default) : null,
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

    internal void WatchAudioLevels() => _audioLevels.Watch();

    internal bool AudioLevelsWanted => _audioLevels.Wanted;

    internal bool WindowVisible => Volatile.Read(ref _windowVisible) != 0;

    internal void SetWindowVisible(bool visible)
    {
        var next = visible ? 1 : 0;
        if (Interlocked.Exchange(ref _windowVisible, next) != next)
            PushWindowVisibility();
    }

    internal void PushWindowVisibility() =>
        _ipc.Broadcast("windowVisibility", JsonSerializer.SerializeToElement(new
        {
            visible = WindowVisible,
        }, Wire.Options));

    private void BroadcastAudioLevels(IReadOnlyDictionary<string, float> levels) =>
        _ipc.Broadcast("audioLevels", JsonSerializer.SerializeToElement(new
        {
            levels = levels.Select(level => new { deviceId = level.Key, peak = level.Value }).ToList(),
        }, Wire.Options));

    internal void PushSettings()
    {
        var displays = EnumerateDisplays();
        _ipc.Broadcast("settings", SettingsMessage.Build(
            _settingsStore.Load(),
            _audioLevels.Devices,
            displays,
            _runtime is null ? null : ObsEncoderPolicy.EnumerateUsableEncoderIds(),
            _primaryDisplay,
            UpdateManager.CurrentInstalledVersion()));
    }

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

    internal void PushGameList()
    {
        var element = JsonSerializer.SerializeToElement(GameList, Wire.Options);
        _ipc.Broadcast("gameList", element);
    }

    private string RelativeToRoot(string absolutePath) => ContentLayout.ToWirePath(EffectiveRoot, absolutePath);

    internal void AddBookmark(AddBookmarkParameters? parameters)
    {
        if (parameters is null || string.IsNullOrEmpty(parameters.FilePath))
            return;

        if (!IsUsableOffsetSeconds(parameters.Time))
        {
            PushError("That bookmark's time is not a real time, so it was not saved.");
            return;
        }

        var bookmark = RecordingBookmarks.Create(parameters);
        var session = _sessionTracker.Active;
        if (session is not null && IsActiveRecordingPath(parameters.FilePath))
        {
            session.AddBookmark(bookmark);
            PushContent();
            return;
        }

        var target = ResolveContentFile(parameters.FilePath);
        if (target is not null && _bookmarks.TryAdd(target, bookmark))
            PushContent();
    }

    internal void DeleteBookmark(DeleteBookmarkParameters? parameters)
    {
        if (parameters is null || string.IsNullOrEmpty(parameters.FilePath))
            return;

        if (!Guid.TryParse(parameters.Id, out var id))
            return;

        var session = _sessionTracker.Active;
        if (session is not null && IsActiveRecordingPath(parameters.FilePath))
        {
            if (session.RemoveBookmark(id))
                PushContent();
            return;
        }

        var target = ResolveContentFile(parameters.FilePath);
        if (target is not null && _bookmarks.TryRemove(Path.GetFileName(target), id))
            PushContent();
    }

    private bool IsActiveRecordingPath(string relativePath)
    {
        if (!IsRecording || _activeSessionPath is not { Length: > 0 })
            return false;

        return string.Equals(relativePath, RelativeToRoot(_activeSessionPath), ContentPathComparison);
    }
}
