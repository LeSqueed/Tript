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
    private readonly ClipTitleStore _clipTitles;
    private readonly ThumbnailStore _thumbnails;
    private readonly TrashStore _trash;
    private readonly GameCatalog _gameCatalog;
    private readonly GameIdAliasStore _gameIdAliases;
    private readonly GameModelManager? _modelManager;
    private readonly ResolverClient? _resolverClient;
    private readonly bool _ownsResolverClient;
    private readonly Timer? _modelCheckTimer;
    private readonly object _audioLevelGate = new();
    private Timer? _audioLevelTimer;
    private static readonly TimeSpan AudioLevelLease = TimeSpan.FromSeconds(5);
    private long _audioLevelsWantedUntilTicks;
    private string? _lastAudioLevelFailure;
    private int _windowVisible = 1;
    private readonly ObsAudioLevelMonitor? _audioLevelMonitor;
    private readonly AudioDeviceInventory _audioDeviceInventory;
    private readonly object _audioDeviceRefreshGate = new();
    private Timer? _audioDeviceTimer;

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
    private readonly object _contentPushGate = new();
    private bool _contentPushRunning;
    private bool _contentPushPending;

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

    private bool _liveHighlightsEnabledAtSessionStart;
    private CancellationTokenSource? _captureWaitCancellation;
    private int _recordingStopRequested;
    private readonly DetectedGameTracker _detectedGames = new();

    private readonly object _gameListGate = new();
    private List<GameInfo> _catalogueGames = [];
    private LibraryGames? _libraryGames;
    private IClipEngine? _clipEngine;
    private readonly object _clipQueueGate = new();
    private readonly Queue<ClipRequest> _clipQueue = [];
    private bool _clipQueueActive;
    private readonly object _sdrConversionGate = new();
    private readonly HashSet<string> _sdrConversions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reservedClipOutputs = new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan TrashPurgeInterval = TimeSpan.FromHours(1);

    private Timer? _trashPurgeTimer;

    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);

    private readonly UpdateManager? _updateManager;
    private Timer? _updateCheckTimer;
    private string? _lastNotifiedUpdateStage;

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

    private static readonly TimeSpan LiveHighlightBoundaryGrace = TimeSpan.FromSeconds(1);

    internal AppHost(AppOptions options, SettingsStore settingsStore, ObsRuntime? runtime,
        RecordingSessionTracker sessionTracker, DisplaySize? primaryDisplay = null,
        TimeSpan? recorderStopTimeout = null, bool enableModelDelivery = false,
        ResolverClient? resolverClient = null,
        AudioDeviceInventory? audioDeviceInventory = null,
        GameIdAliasStore? gameIdAliases = null)
    {
        _options = options;
        _settingsStore = settingsStore;
        _runtime = runtime;
        _audioLevelMonitor = runtime is null ? null : new ObsAudioLevelMonitor();
        _audioDeviceInventory = audioDeviceInventory ?? new AudioDeviceInventory();
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

        EffectiveRoot = Path.GetFullPath(ResolveEffectiveRoot(options, settingsStore.Load()));

        _controller = new AppController(this);
        _ipc = new IpcServer(_controller, _token, options.ControlPort, options.UiPort);
        _metadata = new RecordingMetadataStore(ContentLayout.MetadataRoot(EffectiveRoot));
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

    private static bool IsAtOrAbove(string candidate, string sensitive)
        => FilePaths.IsAtOrUnder(sensitive, candidate);

    private static string ResolveEffectiveRoot(AppOptions options, SettingsModel settings)
    {
        var configured = settings.Recording.OutputDirectory;
        return string.IsNullOrWhiteSpace(configured) ? options.ContentRoot : configured;
    }

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
            _audioDeviceInventory.Refresh();
            _ipc.Start();
            _content.Start();
            _ui.Start();
            if (_audioLevelMonitor is not null)
                _audioLevelTimer = new Timer(_ => PushAudioLevels(), null, TimeSpan.Zero,
                    TimeSpan.FromMilliseconds(250));
            _audioDeviceTimer = new Timer(_ => RefreshAudioDevices(), null, TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5));

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

    private static void WaitForInFlight(object gate)
    {
        lock (gate)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

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
        _updateCheckTimer?.Dispose();
        if (_updateManager is not null)
        {
            _updateManager.StatusChanged -= OnUpdateStatusChanged;
            _updateManager.Dispose();
        }
        _modelCheckTimer?.Dispose();
        _audioLevelTimer?.Dispose();
        WaitForInFlight(_audioLevelGate);
        _audioDeviceTimer?.Dispose();
        WaitForInFlight(_audioDeviceRefreshGate);
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
                ApplyPatch(candidate, patch.Value);
                if (!ValidateGameList(candidate.Game.GameList, out var validationError,
                    requireExistingExecutables: PatchUpdatesGameList(patch.Value)))
                    return validationError;
                if (PatchUpdatesAutomaticClipWindows(patch.Value)
                    && !ValidateAutomaticClipWindows(candidate, out var windowError))
                    return windowError;
                if (!ValidateHotkeys(candidate, out var hotkeyError))
                    return hotkeyError;

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

        var effectiveRoot = Path.GetFullPath(ResolveEffectiveRoot(_options, settings));
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
                case "hotkeys" when property.Value.ValueKind == JsonValueKind.Object:
                    ApplyObjectPatch(settings.Hotkeys, property.Value);
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

    private void RefreshAudioDevices()
    {
        lock (_audioDeviceRefreshGate)
        {
            if (_disposed || !_audioDeviceInventory.Refresh())
                return;
            PushSettings();
        }
    }

    internal void WatchAudioLevels() =>
        Interlocked.Exchange(ref _audioLevelsWantedUntilTicks, (DateTime.UtcNow + AudioLevelLease).Ticks);

    internal bool AudioLevelsWanted => DateTime.UtcNow.Ticks <= Interlocked.Read(ref _audioLevelsWantedUntilTicks);

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

                if (!AudioLevelsWanted)
                {
                    _audioLevelMonitor.Read([]);
                    return;
                }

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
            catch (Exception exception)
            {
                if (_lastAudioLevelFailure != exception.Message)
                {
                    _lastAudioLevelFailure = exception.Message;
                    Log.Warning(exception, "AppHost: audio levels could not be read.");
                }
            }
        }
    }

    internal void PushSettings()
    {
        var settings = _settingsStore.Load();
        var settingsNode = JsonSerializer.SerializeToNode(settings, SettingsSerialization.Options);

        if (settingsNode?["audio"] is JsonObject audioNode)
            audioNode["devices"] = JsonSerializer.SerializeToNode(_audioDeviceInventory.Snapshot,
                SettingsSerialization.Options);
        var settingsElement = JsonSerializer.Deserialize<JsonElement>(
            settingsNode?.ToJsonString() ?? "{}", SettingsSerialization.Options);
        var displays = EnumerateDisplays();
        _ipc.Broadcast("settings", JsonSerializer.SerializeToElement(new
        {
            settings = settingsElement,

            availableEncoders = _runtime is null ? null : ObsEncoderPolicy.EnumerateUsableEncoderIds(),

            displayResolution = _primaryDisplay is { IsUsable: true } display
                ? (object?)new { width = display.Width, height = display.Height }
                : null,

            availableDisplays = displays?.Select(monitor => new
            {
                id = monitor.Id,
                name = monitor.Name,
                width = monitor.Width,
                height = monitor.Height,
                primary = monitor.Primary,
            }).ToList(),
            displayFallbackWarning = BuildDisplayFallbackWarning(settings.Capture, displays),

            appVersion = UpdateManager.CurrentInstalledVersion(),
        }, Wire.Options));
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
        var clipRecords = _clipTitles.EnumerateRecords();
        var referencedSources = ReferencedSourcePaths(clipRecords);

        foreach (var item in items)
        {
            if (!string.IsNullOrEmpty(item.FileName))
                DeleteOne(item, permanent || item.Permanent, clipRecords, referencedSources, processed);
        }

        ReleaseSourceMetadataOfDeletedClips(clipRecords);
    }

    private HashSet<string> ReferencedSourcePaths(
        IReadOnlyList<(string ClipFileName, ClipTitleRecord Record)> clipRecords)
    {
        var referenced = new HashSet<string>(ContentPathComparer);
        foreach (var (_, record) in clipRecords)
        {
            if (NormalizeSourcePath(record.SourceSessionPath) is { } path)
                referenced.Add(path);
        }

        return referenced;
    }

    private void ReleaseSourceMetadataOfDeletedClips(
        IReadOnlyList<(string ClipFileName, ClipTitleRecord Record)> clipRecordsBeforeDelete)
    {
        try
        {
            var survivingClips = _clipTitles.EnumerateRecords();
            var surviving = new HashSet<string>(
                survivingClips.Select(entry => entry.ClipFileName), ContentPathComparer);

            var candidates = new HashSet<string>(ContentPathComparer);
            foreach (var (clipFileName, record) in clipRecordsBeforeDelete)
            {
                if (surviving.Contains(clipFileName))
                    continue;
                if (NormalizeSourcePath(record.SourceSessionPath) is { } path)
                    candidates.Add(path);
            }

            if (candidates.Count == 0)
                return;

            var trashed = _trash.List();
            if (trashed.Any(entry => entry.ContentType is "clip" or "highlight"))
                return;

            var trashedNames = new HashSet<string>(
                trashed.Select(entry => entry.FileName), ContentPathComparer);
            var stillReferenced = ReferencedSourcePaths(survivingClips);

            foreach (var path in candidates)
            {
                if (stillReferenced.Contains(path))
                    continue;

                var fileName = ContentLayout.FileNameOf(path);
                if (trashedNames.Contains(fileName))
                    continue;

                var video = _content.ResolveWithinRoot(path);
                if (video is not null && File.Exists(video))
                    continue;

                _metadata.Delete(fileName);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warning("could not release source metadata of deleted clips: {Reason}", exception.Message);
        }
    }

    private void DeleteOne(DeleteContentParameters item, bool permanent,
        IReadOnlyList<(string ClipFileName, ClipTitleRecord Record)> clipRecords,
        HashSet<string> referencedSources, HashSet<string> processed, ClipTitleRecord? enumeratedClipRecord = null)
    {
        var target = _content.ResolveWithinRoot(item.FileName);
        if (target is null || !processed.Add(target))
            return;

        var fileName = Path.GetFileName(target);
        var relative = RelativeToRoot(target);
        var contentType = ResolveContentType(item.ContentType, relative);
        if (item.DeleteLinkedHighlights && contentType == "recording"
            && !ContentLayout.IsClipPath(relative))
        {
            DeleteLinkedAutomaticHighlights(target, relative, permanent, clipRecords, referencedSources, processed);
        }

        var keepMetadataForClips = contentType == "recording" && referencedSources.Contains(relative);

        if (permanent)
        {
            UnlinkContent(target, fileName, keepMetadataForClips);
            return;
        }

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
            seed.FileSizeBytes = ContentCatalogue.SafeLength(new FileInfo(target));
            files.Add(new TrashedFile(target, relative));
        }

        if (!keepMetadataForClips)
            AddIfPresent(files, _metadata.PathFor(fileName));
        AddIfPresent(files, _clipTitles.PathFor(fileName));

        using var thumbnailHold = _thumbnails.HoldForRemoval(fileName, ThumbnailStore.ExtractionReleaseWait);
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
            _thumbnails.Delete(fileName);
        }
    }

    private void DeleteLinkedAutomaticHighlights(string sourceTarget, string sourcePath, bool permanent,
        IReadOnlyList<(string ClipFileName, ClipTitleRecord Record)> clipRecords,
        HashSet<string> referencedSources, HashSet<string> processed)
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
            }, permanent, clipRecords, referencedSources, processed, record);
        }
    }

    private void UnlinkContent(string target, string fileName, bool keepMetadataForClips = false)
    {
        using var thumbnailHold = _thumbnails.HoldForRemoval(fileName, ThumbnailStore.ExtractionReleaseWait);
        try
        {
            SharingViolationRetry.Run(() => File.Delete(target));

            var metadataDeleted = keepMetadataForClips || _metadata.Delete(fileName);
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

    private string RelativeToRoot(string absolutePath) => ContentLayout.ToWirePath(EffectiveRoot, absolutePath);

    private static readonly HashSet<string> WireContentTypes =
        new(StringComparer.Ordinal) { "recording", "clip", "highlight", "buffer" };

    private static string ResolveContentType(string? requested, string relativePath)
    {
        if (requested is not null && WireContentTypes.Contains(requested))
            return requested;
        return ContentLayout.IsClipPath(relativePath) ? "clip" : "recording";
    }

    internal void AddBookmark(AddBookmarkParameters? parameters)
    {
        if (parameters is null || string.IsNullOrEmpty(parameters.FilePath))
            return;

        if (!IsUsableOffsetSeconds(parameters.Time))
        {
            PushError("That bookmark's time is not a real time, so it was not saved.");
            return;
        }

        var bookmark = CreateBookmark(parameters);
        var session = _sessionTracker.Active;
        if (session is not null && IsActiveRecordingPath(parameters.FilePath))
        {
            session.AddBookmark(bookmark);
            PushContent();
            return;
        }

        var target = ResolveContentFile(parameters.FilePath);
        if (target is not null && TrySaveBookmark(target, bookmark))
            PushContent();
    }

    private bool TrySaveBookmark(string target, Bookmark bookmark)
    {
        var fileName = Path.GetFileName(target);
        lock (_metadata.WriteGate)
        {
            var existing = _metadata.Read(fileName);
            if (existing.MustNotBeOverwritten)
            {
                Log.Warning("{FileName} has a metadata record that could not be read ({Failure}); the bookmark is refused rather than replacing it.", fileName, existing.Failure);
                PushError(
                    "The bookmark could not be saved: this recording's metadata record could not be read, and overwriting it would lose its game and existing bookmarks.");
                return false;
            }

            var metadata = existing.Record ?? new RecordingMetadata { VideoPath = RelativeToRoot(target) };
            metadata.Bookmarks.Add(bookmark);
            if (_metadata.Save(metadata))
                return true;
        }

        PushError("The bookmark could not be saved, check the recording folder is writable.");
        return false;
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
        if (target is not null && TryRemoveBookmark(Path.GetFileName(target), id))
            PushContent();
    }

    private bool TryRemoveBookmark(string fileName, Guid id)
    {
        lock (_metadata.WriteGate)
        {
            var existing = _metadata.Read(fileName);
            if (existing.MustNotBeOverwritten)
            {
                Log.Warning("{FileName} has a metadata record that could not be read ({Failure}); the bookmark removal is refused rather than replacing it.", fileName, existing.Failure);
                PushError("The bookmark could not be removed: this recording's metadata record could not be read.");
                return false;
            }

            var metadata = existing.Record;
            if (metadata is null || metadata.Bookmarks.RemoveAll(bookmark => bookmark.Id == id) == 0)
                return false;
            if (_metadata.Save(metadata))
                return true;
        }

        PushError("The bookmark could not be removed, check the recording folder is writable.");
        return false;
    }

    private bool IsActiveRecordingPath(string relativePath)
    {
        if (!IsRecording || _activeSessionPath is not { Length: > 0 })
            return false;

        return string.Equals(relativePath, RelativeToRoot(_activeSessionPath), ContentPathComparison);
    }

    private static Bookmark CreateBookmark(AddBookmarkParameters parameters) => new()
    {
        Id = Guid.TryParse(parameters.Id, out var id) ? id : Guid.NewGuid(),
        Type = Enum.TryParse<BookmarkType>(parameters.Type, ignoreCase: true, out var type) ? type : BookmarkType.Manual,
        Time = TimeSpan.FromSeconds(parameters.Time),
    };
}
