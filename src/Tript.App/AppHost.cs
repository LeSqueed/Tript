// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;
using Tript.App.Content;
using Tript.App.Ipc;
using Tript.Detection;
using Tript.Media;
using Tript.Obs;
using Tript.Recorder;
using Tript.Settings;
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
internal sealed class AppHost : IDisposable
{
    private const string StartupGameId = "Overwatch";

    private readonly AppOptions _options;
    private readonly SettingsStore _settingsStore;
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
    private bool _shuttingDown;

    private RecorderStateMachine? _recorder;
    private IRecorderSession? _recorderSession;
    private ObsSource? _colourSource;
    private ProcessNameGameDetector? _detector;
    private DetectionHost? _detectionHost;
    private RecordingMetadata? _pendingMetadata;
    private string? _activeOutputPath;
    private CancellationTokenSource? _captureWaitCancellation;
    private int _recordingStopRequested;

    // Replaced wholesale under _gameListGate, never mutated in place: GameList is read from the
    // IPC pool, the detector's timer and the hook probe, and a reader holding the old list must
    // be able to finish enumerating it.
    private readonly object _gameListGate = new();
    private List<GameInfo> _catalogueGames = [];
    private IClipEngine? _clipEngine;

    // The bin is swept at startup and once an hour after it, so a host that stays up for days still
    // honours the retention.
    private static readonly TimeSpan TrashPurgeInterval = TimeSpan.FromHours(1);

    private Timer? _trashPurgeTimer;

    private bool _disposed;

    // primaryDisplay is optional: a host built without one just pushes no display resolution.
    internal AppHost(AppOptions options, SettingsStore settingsStore, ObsRuntime? runtime,
        RecordingSessionTracker sessionTracker, DisplaySize? primaryDisplay = null)
    {
        _options = options;
        _settingsStore = settingsStore;
        _runtime = runtime;
        _sessionTracker = sessionTracker;
        _primaryDisplay = primaryDisplay;

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

    // The single root everything content lives under: sessions, clips, the metadata tree and the
    // content server's traversal guard all resolve against it. A configured Recording.OutputDirectory
    // wins; empty falls back to the content root, and a settings change updates it in place.
    internal string EffectiveRoot { get; private set; }

    // Why a recording directory is refused, or null when it is fine.
    //
    // The content server serves everything under this root, and it deliberately checks no Origin —
    // a media element sends none, so requiring one would break the app's own player. That is only
    // safe while the root is a folder of recordings. Pointing it at "/" or at the home directory
    // turns http://localhost:2222/api/content/<path> into a reader for the whole machine, for any
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
    {
        var configured = settingsStore.Load().Recording.OutputDirectory;
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

        // No browser is opened — the desktop shell renders the UI in its own window.
        WaitForShutdown();
    }

    private void WaitForShutdown()
    {
        var shutdownRequested = new ManualResetEventSlim(false);
        _ipc.ShutdownRequested += shutdownRequested.Set;
        if (_ipc.ShutdownWasRequested)
            shutdownRequested.Set();

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
        lock (_recorderGate)
            _shuttingDown = true;

        _trashPurgeTimer?.Dispose();
        _detectionHost?.Dispose();
        _detector?.Dispose();

        // Preserve recording metadata when possible, but never let a dead recorder block shutdown.
        try
        {
            StopRecording();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Tript.App: recording cleanup failed during shutdown: {exception.Message}");
        }

        lock (_recorderGate)
        {
            _recorder?.Dispose();
            _recorderSession?.Dispose();
            _colourSource?.Dispose();
        }

        _ipc.Dispose();
        _content.Dispose();
        _ui.Dispose();
        _runtime?.Dispose();
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
            PushError("There was no recording to stop.");
    }

    internal bool StartRecording(string? gameId)
    {
        lock (_recorderGate)
            return StartRecordingLocked(gameId);
    }

    private bool StartRecordingLocked(string? gameId)
    {
        var effectiveGameId = gameId ?? StartupGameId;

        // The detector's handlers can still fire once after teardown began: its Dispose no longer
        // blocks behind an in-flight callback, deliberately.
        if (_shuttingDown)
            return false;

        if (_recorder is not null && _recorder.Snapshot.State != RecorderState.Idle)
            return false;

        var settings = _settingsStore.Load();
        var resolved = SettingsResolver.Resolve(settings, effectiveGameId);

        // The app records Session only; a resolved Hybrid is flattened so the recorder accepts the start.
        if (resolved.Mode == RecordingMode.Hybrid)
            resolved.Mode = RecordingMode.Session;
        if (resolved.Mode != RecordingMode.Session)
            return false;

        resolved.OutputPath = BuildOutputPath(settings);

        EnsureRecorderBuilt(resolved);

        // Point the session's game-capture source at the detected game before the recording starts, so
        // the recording shows the game rather than the background. win-capture keeps retrying the hook
        // while the source is shown, so a game that appears mid-recording is still picked up.
        RetargetGameCapture(effectiveGameId);

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
                    () => PushWarning("Waiting for the game window to appear. Recording has not started yet."),
                    () => PushWarning(null), waitCancellation.Token);
                if (!captureReady)
                    return false;
            }
            finally
            {
                _captureWaitCancellation = null;
                PushWarning(null);
                capture.ClearSourceFromChannel();
            }
            waitCancellation.Dispose();
        }

        if (!_recorder!.Start(resolved))
            return false;

        _activeOutputPath = resolved.OutputPath;
        _currentGameId = effectiveGameId;
        _pendingMetadata = new RecordingMetadata
        {
            Game = GameList.FirstOrDefault(g => g.Id == effectiveGameId)?.Name ?? effectiveGameId,
            ContentType = ContentType.Recording,
            StartTime = DateTime.Now,
        };

        _sessionTracker.Start(_pendingMetadata.StartTime);

        StartDetection(effectiveGameId);

        PushState(recording: true, effectiveGameId);
        RequestNotification(NotificationKind.RecordingStarted, "Recording started",
            string.IsNullOrWhiteSpace(effectiveGameId) ? "Tript is recording." : $"Tript is recording {effectiveGameId}.");
        return true;
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

        if (!_recorder.Stop())
            return false;

        // The recorder marshals the transition onto the thread it was created on; the IPC thread is that
        // thread for the real recorder, and the fake raises the signal synchronously inside Stop.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (_recorder.Snapshot.State != RecorderState.Idle)
        {
            if (DateTime.UtcNow > deadline)
                break;
            Thread.Sleep(20);
        }

        StopDetection();

        var session = _sessionTracker.Stop();
        if (session is not null && _pendingMetadata is not null)
        {
            _pendingMetadata.Bookmarks = session.Bookmarks.ToList();
            WriteMetadataRecord(_pendingMetadata);
        }

        _pendingMetadata = null;
        _activeOutputPath = null;
        _currentGameId = null;

        PushState(recording: false, null);
        RequestNotification(NotificationKind.RecordingStopped, "Recording stopped", "The recording is ready in your library.");
        return true;
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

    // ---- detection ----

    private void WireAutoStart()
    {
        if (_options.FakeRecorder)
            return;

        if (_recorder is null)
            EnsureRecorderBuilt(SettingsResolver.Resolve(_settingsStore.Load()));

        if (_recorder is null)
            return;

        // Executables, not display names: the detector matches the running process list, and a game
        // whose display name differs from its executable ("Counter-Strike 2" / cs2.exe) would never
        // be seen if the name were watched instead.
        var executables = GameList
            .Select(ExecutableOf)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (executables.Count == 0)
            return;

        // The detector is subscribed straight to the same host methods the IPC path uses, so an
        // auto-recorded session gets the whole lifecycle — metadata sidecar, session tracking, detection
        // and state pushes — rather than a bare recorder Start/Stop.
        // What the detector can actually report, as catalogue ids — the vocabulary PushState asks it
        // about. Left empty, `game.detected` on every state push was permanently false.
        _detectorGameNames.Clear();
        foreach (var id in WatchableGameIds(GameList))
            _detectorGameNames.Add(id);

        _detector = new ProcessNameGameDetector(executables);
        _detector.GameStarted += processName => StartRecording(ResolveDetectedGameId(processName));
        _detector.GameStopped += () =>
        {
            _captureWaitCancellation?.Cancel();
            StopRecording();
        };
        _detector.Start();
    }

    // The catalogue ids a process watcher could report. Pure and separate from WireAutoStart because
    // that method needs a real recorder, so nothing reachable from a test would otherwise cover it.
    internal static IReadOnlyList<string> WatchableGameIds(IEnumerable<GameInfo> games) =>
        games.Where(game => ExecutableOf(game).Length > 0 && game.Id.Length > 0)
            .Select(game => game.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

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

    private void StartDetection(string gameId)
    {
        _detectionHost?.Stop();
        _detectionHost?.Dispose();
        _detectionHost = null;

        var detector = new VisualEventDetectorAdapter(new VisualEventDetector());
        _detectionHost = new DetectionHost(detector);
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

    // ---- settings ----

    internal bool UpdateSettings(JsonElement? patch)
    {
        if (patch is null)
            return false;

        var settings = _settingsStore.Load();
        var previousOutputDirectory = settings.Recording.OutputDirectory;
        try
        {
            ApplyPatch(settings, patch.Value);
        }
        catch (JsonException)
        {
            return false;
        }

        _settingsStore.Save();

        // The catalogue is built from these settings, so this is the one moment it can change.
        ReloadGameList();

        // A changed OutputDirectory takes effect without a restart: the effective root, the content
        // server's guard root and the metadata store are all rebuilt.
        var effectiveRoot = Path.GetFullPath(ResolveEffectiveRoot(_options, _settingsStore));
        if (!string.Equals(effectiveRoot, EffectiveRoot, StringComparison.Ordinal))
        {
            if (UnsafeRecordingRoot(effectiveRoot) is { } refusal)
            {
                // The previous root stays; adopting this one half-way would leave the stores and the
                // content server disagreeing about where content lives. The setting goes back with
                // it: a refused directory left in the file would be shown as the recording folder by
                // the push below, and adopted unguarded by the next launch.
                settings.Recording.OutputDirectory = previousOutputDirectory;
                _settingsStore.Save();

                Console.Error.WriteLine($"Tript.App: refused recording directory '{effectiveRoot}': {refusal}");
                PushError($"That folder was not used as the recording directory: {refusal}");
                PushSettings();
                return true;
            }

            EffectiveRoot = effectiveRoot;
            _content.UpdateRoot(effectiveRoot);
            _metadata.UpdateRoot(Path.Combine(effectiveRoot, "metadata"));
            _clipTitles.UpdateRoot(Path.Combine(effectiveRoot, "metadata"));
            _thumbnails.UpdateRoot(ThumbnailRootFor(effectiveRoot));
            _trash.UpdateRoot(TrashRootFor(effectiveRoot));
            Directory.CreateDirectory(effectiveRoot);
        }

        SettingsChanged?.Invoke(settings);
        PushSettings();
        return true;
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
        var games = AppOptions.LoadCatalogue(_settingsStore.Load(), _options.GameListJson);
        lock (_gameListGate)
            _catalogueGames = games;
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
        var game = gameId is null ? null : new GameInfo
        {
            Id = gameId,
            Name = GameList.FirstOrDefault(g => g.Id == gameId)?.Name ?? gameId,
            Detected = _detector is not null && _detectorGameNames.Contains(gameId),
        };

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
            },
        }, Wire.Options));
        StateChanged?.Invoke(recording, gameId);
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

    // The catalogue ids the auto-start detector is watching, so a state push can say whether the
    // game being recorded is one it found itself.
    private readonly HashSet<string> _detectorGameNames = new(StringComparer.OrdinalIgnoreCase);

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
            availableEncoders = _runtime is null ? null : ObsRecorderSession.EnumerateUsableEncoderIds(),
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

    // ---- content ----

    // ContentServer holds the same root (it owns the traversal guard); this is for callers that need
    // the root without the server.
    internal string ContentRoot => EffectiveRoot;

    // Rebuilds the content catalogue from disk and broadcasts it. Pushed whenever the catalogue
    // changes — after a recording stops, a clip completes, a rename, or a delete.
    internal void PushContent()
    {
        _ipc.Broadcast("content", JsonSerializer.SerializeToElement(new
        {
            content = ListContent(),
        }, Wire.Options));
    }

    // Surfaces a failure to the user, so a bookmark, title or delete that did not go through does
    // not silently vanish.
    // An offset a TimeSpan can actually hold. TimeSpan.FromSeconds throws ArgumentException on NaN
    // and OverflowException well below double's range, so both ends are checked before conversion.
    private static bool IsUsableOffsetSeconds(double seconds) =>
        double.IsFinite(seconds) && seconds >= 0 && seconds < TimeSpan.MaxValue.TotalSeconds;

    private void PushError(string message)
    {
        _ipc.Broadcast("error", JsonSerializer.SerializeToElement(new
        {
            message,
        }, Wire.Options));
        RequestNotification(NotificationKind.Error, "Tript error", message);
    }

    private void RequestNotification(NotificationKind kind, string title, string body) =>
        NotificationRequested?.Invoke(kind, title, body);

    private void PushWarning(string? message)
    {
        _ipc.Broadcast("warning", JsonSerializer.SerializeToElement(
            message is null ? null : new { message }, Wire.Options));
    }

    // How many previously-unseen files one call may probe for a duration — what keeps a first list of
    // a large existing library from becoming an ffprobe per item in one go.
    private const int DurationProbeBudget = 12;

    // The library, rebuilt from disk. Newest first, and a TOTAL order: the frontend paginates over
    // this list, so two items with the same timestamp must not be able to swap places between pushes
    // (EnumerateFiles' order is the file system's, not one we can rely on).
    internal List<ContentItem> ListContent()
    {
        var items = new List<ContentItem>();
        var root = new DirectoryInfo(EffectiveRoot);
        if (!root.Exists)
            return items;

        // Recording base name -> game, used to give the clips a game after the loop.
        var gamesByRecording = new Dictionary<string, string>(StringComparer.Ordinal);
        var tracksByRecording = new Dictionary<string, List<AudioTrackInfo>>(StringComparer.Ordinal);
        var clips = new List<ContentItem>();
        var probeBudget = DurationProbeBudget;

        foreach (var file in root.EnumerateFiles("*.mp4", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(EffectiveRoot, file.FullName)
                .Replace(Path.DirectorySeparatorChar, '/');
            // Deleted content is still on disk until it is purged; the library must not show it.
            if (IsTrashPath(relative))
                continue;

            var topLevel = TopLevelDirectory(relative);
            var contentType = topLevel.Equals("clips", StringComparison.Ordinal) ? "clip" : "recording";

            var item = new ContentItem
            {
                ContentType = contentType,
                FileName = file.Name,
                FilePath = relative,
                Title = Path.GetFileNameWithoutExtension(file.Name),
                FileSizeBytes = SafeLength(file),
            };

            if (contentType == "recording")
            {
                var metadata = _metadata.Load(file.Name);
                if (metadata is not null)
                {
                    item.Bookmarks = metadata.Bookmarks
                        .Select(bookmark => new BookmarkItem
                        {
                            Id = bookmark.Id.ToString(),
                            Type = bookmark.Type.ToString().ToLowerInvariant(),
                            Subtype = bookmark.Subtype,
                            Time = bookmark.Time.TotalSeconds,
                        })
                        .ToList();
                    item.Title = string.IsNullOrWhiteSpace(metadata.Title) ? item.Title : metadata.Title;
                    item.Favorite = metadata.Favorite;
                    item.StartTime = DateTimeToUnixSeconds(metadata.StartTime);
                    item.Game = string.IsNullOrWhiteSpace(metadata.Game) ? null : metadata.Game;
                    item.DurationSeconds = metadata.DurationSeconds;
                    item.AudioTracks = ToAudioTrackInfo(metadata);

                    var baseName = Path.GetFileNameWithoutExtension(file.Name);
                    if (item.Game is not null)
                        gamesByRecording[baseName] = item.Game;
                    if (item.AudioTracks is not null)
                        tracksByRecording[baseName] = item.AudioTracks;
                }
                else
                {
                    // A session with no metadata record still lists — empty bookmarks, no title.
                    item.Bookmarks = [];
                }
            }
            else
            {
                // A clip without a stored title falls back to its file name. Its own record carries the duration.
                var record = _clipTitles.LoadRecord(file.Name);
                if (!string.IsNullOrWhiteSpace(record?.Title))
                    item.Title = record.Title;
                item.Favorite = record?.Favorite ?? false;
                item.DurationSeconds = record?.DurationSeconds;

                // Resolved after the loop: the source session may be listed after its clip.
                clips.Add(item);
            }

            // A metadata record's StartTime is the authoritative capture time; the file's last-write time is
            // the fallback for content that has no record — every clip, and anything copied in by hand.
            item.StartTime ??= DateTimeToUnixSeconds(file.LastWriteTime);

            // Spent only on files that will actually be probed, so a known-unreadable file cannot consume a
            // slot a real recording later in the enumeration needs.
            if (item.DurationSeconds is null && probeBudget > 0 && !IsUnprobeable(file.FullName))
            {
                probeBudget--;
                item.DurationSeconds = TryReadDuration(file, relative, contentType == "recording");
            }

            items.Add(item);
        }

        foreach (var clip in clips)
        {
            clip.Game = InheritedGame(clip.FileName, gamesByRecording);
            // A clip keeps every audio track of the session it was cut from, so it keeps the names.
            clip.AudioTracks = InheritedFrom(clip.FileName, tracksByRecording);
        }

        // The relative path is the tiebreak so the order is total: List.Sort is unstable, and two files
        // written in the same second would otherwise shuffle a paginated grid under the user.
        items.Sort((left, right) =>
        {
            var byDate = (right.StartTime ?? 0).CompareTo(left.StartTime ?? 0);
            return byDate != 0 ? byDate : string.CompareOrdinal(left.FilePath, right.FilePath);
        });

        return items;
    }

    // The game a clip inherits from the session it was cut from. A clip has no metadata record of
    // its own and nothing on the wire carries the game into CreateClip's output, so the file name
    // is the link: both naming paths start the name with the source session's base name.
    private static string? InheritedGame(string clipFileName, Dictionary<string, string> gamesByRecording)
        => InheritedFrom(clipFileName, gamesByRecording);

    // What a clip inherits from the session it was cut from. A clip has no metadata record of its
    // own and nothing on the wire carries this into CreateClip's output, so the file name is the
    // link: both naming paths start the name with the source session's base name. The longest
    // matching session wins, so a session whose name is a prefix of another cannot claim its clips.
    private static TValue? InheritedFrom<TValue>(string clipFileName, Dictionary<string, TValue> bySession)
        where TValue : class
    {
        var clipBaseName = Path.GetFileNameWithoutExtension(clipFileName);
        TValue? inherited = null;
        var matched = 0;

        foreach (var (recording, value) in bySession)
        {
            if (recording.Length <= matched)
                continue;
            if (!clipBaseName.StartsWith(recording, StringComparison.Ordinal))
                continue;
            if (clipBaseName.Length != recording.Length && clipBaseName[recording.Length] != '-')
                continue;

            inherited = value;
            matched = recording.Length;
        }

        return inherited;
    }

    // The layout a recording's metadata declares, in stream order. Null rather than empty when the
    // record names no tracks, so "unknown" and "no audio" stay distinguishable on the wire.
    private static List<AudioTrackInfo>? ToAudioTrackInfo(RecordingMetadata metadata)
    {
        if (metadata.AudioTracks.Count == 0)
            return null;

        return metadata.AudioTracks
            .OrderBy(track => track.Index)
            .Select(track => new AudioTrackInfo { Index = track.Index, Name = track.Name })
            .ToList();
    }

    // Reads a file's duration and persists it, so it is read once per file and served from the
    // record forever after. Probing is bounded rather than avoided.
    private double? TryReadDuration(FileInfo file, string relativePath, bool isRecording)
    {
        var probe = LibraryProbe;
        if (probe is null)
            return null;

        double seconds;
        try
        {
            seconds = probe.Probe(file.FullName).DurationSeconds;
        }
        catch (Exception exception)
        {
            // A text file with an .mp4 name, a truncated recording, an ffprobe that will not start: the item
            // still lists, just without a length, and it is not probed again this process.
            Console.Error.WriteLine($"Tript.App: could not read the duration of '{relativePath}': {exception.Message}");
            MarkUnprobeable(file.FullName);
            return null;
        }

        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0)
        {
            // ffprobe reports no duration for some containers; MediaProbe normalises that to NaN.
            MarkUnprobeable(file.FullName);
            return null;
        }

        // Best-effort: a write failure costs one probe on the next push, which is not worth
        // surfacing for a duration label. The read-modify-write is over Read, not Load, and that
        // difference is the whole point.
        if (isRecording)
        {
            _metadata.SaveDuration(file.Name, relativePath, seconds);
        }
        else
        {
            // The clip store makes the same distinction internally: a clip's record carries its user title.
            _clipTitles.SaveDuration(file.Name, seconds);
        }

        return seconds;
    }

    private void MarkUnprobeable(string absolutePath)
    {
        lock (_unprobeable)
            _unprobeable.Add(absolutePath);
    }

    private bool IsUnprobeable(string absolutePath)
    {
        lock (_unprobeable)
            return _unprobeable.Contains(absolutePath);
    }

    // Separate from the clip engine's probe only because the engine is built on demand; both are just
    // an ffprobe path plus a per-path cache.
    private MediaProbe? LibraryProbe
    {
        get
        {
            var tools = _libraryTools.Value;
            if (tools is null)
                return null;

            // Deliberately unguarded. A clip finishing pushes content from its own thread while the IPC thread
            // may be listing, so two probes can be built; a reference assignment cannot tear, MediaProbe locks
            // its own cache, and the loser only costs a cold cache.
            return _libraryProbe ??= new MediaProbe(tools.Value.Ffprobe);
        }
    }

    private static long SafeLength(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The file went away between the enumeration and this read.
            return 0;
        }
    }

    // Classifies an item by its top-level directory (sessions/ -> recording, clips/ -> clip).
    private static string TopLevelDirectory(string relativePath)
    {
        var separator = relativePath.IndexOf('/');
        return separator >= 0 ? relativePath[..separator] : relativePath;
    }

    private static bool IsTrashPath(string relativePath) =>
        relativePath.StartsWith(TrashStore.DirectoryName + "/", StringComparison.Ordinal);

    private static double? DateTimeToUnixSeconds(DateTime dateTime)
        => dateTime == default ? null : new DateTimeOffset(dateTime).ToUnixTimeSeconds();

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
        return TopLevelDirectory(relativePath).Equals("clips", StringComparison.Ordinal) ? "clip" : "recording";
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
        if (TopLevelDirectory(relative).Equals("clips", StringComparison.Ordinal))
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
        if (TopLevelDirectory(relative).Equals("clips", StringComparison.Ordinal))
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

            // Orphaned means not part of the catalogue the library builds. A file in clips/ is never orphaned.
            if (IsTrashPath(normalized))
                continue;
            if (normalized.StartsWith("sessions/", StringComparison.Ordinal))
                continue;
            if (normalized.StartsWith("clips/", StringComparison.Ordinal))
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

    // Sessions are flat under <effectiveRoot>/sessions/ — the timestamp is already in the file name.
    private string BuildOutputPath(SettingsModel settings)
    {
        var directory = Path.Combine(EffectiveRoot, "sessions");
        Directory.CreateDirectory(directory);
        // Millisecond resolution keeps two sessions started in the same second from colliding on one file
        // name, which would overwrite the recording and its metadata record.
        var name = $"session-{DateTime.Now:yyyyMMdd-HHmmssfff}.mp4";
        return Path.Combine(directory, name);
    }
}
