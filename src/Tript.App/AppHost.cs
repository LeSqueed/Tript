// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
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

// Assembles every component into the running alpha. Owns the process lifetime:
//
//   * the settings store and the recording-session tracker (already registered at startup),
//   * the recorder + process-game detector + detection host (the recording path),
//   * the three local IPC channels: control socket, content server, UI host,
//   * the content catalogue (what the library and the content server serve),
//   * the clip pipeline (IClipEngine) driven by CreateClip.
//
// State pushes: state on recorder transitions, settings after every settings mutation, gameList on
// NewConnection and when the detection state changes. Every push is a full push (spec/local-ipc.md:
// the frontend converges to a consistent view).
internal sealed class AppHost : IDisposable
{
    private const string StartupGameId = "Overwatch";

    private readonly AppOptions _options;
    private readonly SettingsStore _settingsStore;
    private readonly ObsRuntime? _runtime;
    private readonly RecordingSessionTracker _sessionTracker;

    private readonly AppController _controller;
    private readonly IpcServer _ipc;
    private readonly ContentServer _content;
    private readonly UiHost _ui;

    private RecorderStateMachine? _recorder;
    private IRecorderSession? _recorderSession;
    private ObsSource? _colourSource;
    private ProcessNameGameDetector? _detector;
    private DetectionHost? _detectionHost;
    private RecordingMetadata? _pendingMetadata;
    private string? _activeOutputPath;

    private readonly List<GameInfo> _catalogueGames = [];
    private IClipEngine? _clipEngine;

    private bool _disposed;

    internal AppHost(AppOptions options, SettingsStore settingsStore, ObsRuntime? runtime,
        RecordingSessionTracker sessionTracker)
    {
        _options = options;
        _settingsStore = settingsStore;
        _runtime = runtime;
        _sessionTracker = sessionTracker;

        _controller = new AppController(this);
        _ipc = new IpcServer(_controller);
        _content = new ContentServer(options.ContentRoot);
        _ui = new UiHost(options.WebRoot);

        Directory.CreateDirectory(options.ContentRoot);
        ReloadGameList();
    }

    internal AppOptions Options => _options;

    internal SettingsStore SettingsStore => _settingsStore;

    internal ObsRuntime? Runtime => _runtime;

    internal IpcServer Ipc => _ipc;

    internal ContentServer Content => _content;

    internal bool IsRecording => _recorder is not null && _recorder.Snapshot.State != RecorderState.Idle;

    internal string? CurrentGameId => _recorder is not null && _recorder.Snapshot.State != RecorderState.Idle
        ? _currentGameId
        : null;

    private string? _currentGameId;

    // ---- lifetime ----

    public void Run()
    {
        _ipc.Start();
        _content.Start();
        _ui.Start();

        WireAutoStart();

        // The single-line contract the smoke test waits for; the frontend's LiveIpcProbe also
        // depends on the host being reachable once this line appears.
        Console.WriteLine("READY");
        Console.Out.Flush();

        // The UI is served over HTTP at the UI host; the URL is the contract on stdout. No browser
        // is opened — the desktop shell renders the UI in its own window.
        WaitForShutdown();
    }

    private void WaitForShutdown()
    {
        var shutdownRequested = new ManualResetEventSlim(false);
        _ipc.ShutdownRequested += shutdownRequested.Set;

        while (!shutdownRequested.IsSet)
            Thread.Sleep(100);

        Console.WriteLine("SHUTDOWN");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _detectionHost?.Dispose();
        _detector?.Dispose();
        _recorder?.Dispose();
        _recorderSession?.Dispose();
        _colourSource?.Dispose();
        _ipc.Dispose();
        _content.Dispose();
        _ui.Dispose();
        _runtime?.Dispose();
    }

    // ---- recorder wiring ----

    internal bool StartRecording(string? gameId)
    {
        var effectiveGameId = gameId ?? StartupGameId;

        if (_recorder is not null && _recorder.Snapshot.State != RecorderState.Idle)
            return false;

        var settings = _settingsStore.Load();
        var resolved = SettingsResolver.Resolve(settings, effectiveGameId);

        // The alpha records Session only; the default mode is Hybrid (designed for, deferred). A
        // resolved Hybrid is flattened to Session so the recorder accepts the start.
        if (resolved.Mode == RecordingMode.Hybrid)
            resolved.Mode = RecordingMode.Session;
        if (resolved.Mode != RecordingMode.Session)
            return false;

        resolved.OutputPath = BuildOutputPath(settings);

        EnsureRecorderBuilt(resolved);

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
        return true;
    }

    internal bool StopRecording()
    {
        if (_recorder is null || _recorder.Snapshot.State == RecorderState.Idle)
            return false;

        if (!_recorder.Stop())
            return false;

        // Wait for the stop signal to complete the transition back to Idle. The recorder marshals
        // the transition onto the thread it was created on; the IPC thread is that thread for the
        // real recorder, and the fake raises the signal synchronously inside Stop.
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
            WriteMetadataSidecar(_pendingMetadata);
        }

        _pendingMetadata = null;
        _activeOutputPath = null;
        _currentGameId = null;

        PushState(recording: false, null);
        return true;
    }

    // Persists the recording's metadata — game, start time, content type, audio tracks and the
    // automatic bookmarks the detection host produced (spec/config-and-storage.md) — next to the
    // file, so a finished recording carries its metadata on disk and bookmarks survive the process.
    // The .bookmarks.json sidecar is the user-driven bookmark path (AddBookmark/DeleteBookmark) and
    // is left alone here. A failed start leaves no file, so the sidecar is written only when the
    // recording actually exists.
    private void WriteMetadataSidecar(RecordingMetadata metadata)
    {
        if (_activeOutputPath is null || !File.Exists(_activeOutputPath))
            return;

        var sidecar = _activeOutputPath + ".metadata.json";
        try
        {
            File.WriteAllText(sidecar, JsonSerializer.Serialize(metadata, SettingsSerialization.Options));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Tript.App: could not write metadata sidecar: {exception.Message}");
        }
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

    // ---- detection ----

    private void WireAutoStart()
    {
        if (_options.FakeRecorder)
            return;

        if (_recorder is null)
            EnsureRecorderBuilt(SettingsResolver.Resolve(_settingsStore.Load()));

        if (_recorder is null)
            return;

        var gameNames = GameList.Select(game => game.Name).Where(name => name.Length > 0).Distinct().ToList();
        if (gameNames.Count == 0)
            return;

        // The auto-start seam subscribes the detector straight to the same host methods the IPC
        // path uses, so an auto-recorded session gets the whole lifecycle — metadata sidecar,
        // session tracking, detection and state pushes — rather than a bare recorder Start/Stop.
        // Resolving the detected game's settings is StartRecording's own job. The booleans the
        // methods return are the start/stop refusal channel, which the detector's lifecycle does
        // not need to see.
        _detector = new ProcessNameGameDetector(gameNames);
        _detector.GameStarted += name => StartRecording(name);
        _detector.GameStopped += () => StopRecording();
        _detector.Start();
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
        }
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
        try
        {
            ApplyPatch(settings, patch.Value);
        }
        catch (JsonException)
        {
            return false;
        }

        _settingsStore.Save();
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
            }
        }
    }

    private static void ApplyObjectPatch(object page, JsonElement patch)
    {
        // The page objects carry JsonExtensionData, so round-tripping the page through the
        // serializer with the patch merged preserves unknown keys. The serializer options are the
        // settings model's own (camelCase, string enums).
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
    }

    private static string MergeObjects(JsonElement baseObject, JsonElement patch)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in baseObject.EnumerateObject())
                property.WriteTo(writer);
            foreach (var property in patch.EnumerateObject())
                property.WriteTo(writer);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    // ---- game list ----

    internal void ReloadGameList()
    {
        _catalogueGames.Clear();
        var settings = _settingsStore.Load();
        _catalogueGames.AddRange(AppOptions.LoadCatalogue(settings, _options.GameListJson));
    }

    internal List<GameInfo> GameList
    {
        get
        {
            if (_catalogueGames.Count == 0)
                ReloadGameList();
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
            },
        }, Wire.Options));
    }

    private readonly HashSet<string> _detectorGameNames = [];

    internal void PushSettings()
    {
        var settings = _settingsStore.Load();
        var settingsElement = JsonSerializer.SerializeToElement(settings, SettingsSerialization.Options);
        _ipc.Broadcast("settings", JsonSerializer.SerializeToElement(new
        {
            settings = settingsElement,
        }, Wire.Options));
    }

    internal void PushGameList()
    {
        var element = JsonSerializer.SerializeToElement(GameList, Wire.Options);
        _ipc.Broadcast("gameList", element);
    }

    // ---- content ----

    internal string ContentRoot => _options.ContentRoot;

    internal List<ContentItem> ListContent()
    {
        var items = new List<ContentItem>();
        var root = new DirectoryInfo(_options.ContentRoot);
        if (!root.Exists)
            return items;

        foreach (var file in root.EnumerateFiles("*.mp4", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(_options.ContentRoot, file.FullName)
                .Replace(Path.DirectorySeparatorChar, '/');
            items.Add(new ContentItem
            {
                ContentType = "recording",
                FileName = file.Name,
                FilePath = relative,
                Title = Path.GetFileNameWithoutExtension(file.Name),
            });
        }

        return items;
    }

    // ---- content operations ----

    internal void DeleteContent(DeleteContentParameters? parameters)
    {
        if (parameters is null || string.IsNullOrEmpty(parameters.FileName))
            return;

        var target = ResolveContentFile(parameters.FileName);
        if (target is null)
            return;

        try
        {
            File.Delete(target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Tript.App: could not delete '{target}': {exception.Message}");
        }
    }

    internal void DeleteMultipleContent(DeleteMultipleContentParameters? parameters)
    {
        if (parameters?.Items is null)
            return;
        foreach (var item in parameters.Items)
            DeleteContent(item);
    }

    internal void RenameContent(RenameContentParameters? parameters)
    {
        if (parameters is null || string.IsNullOrEmpty(parameters.FileName))
            return;

        var target = ResolveContentFile(parameters.FileName);
        if (target is null)
            return;

        // The alpha renames by writing a sidecar title next to the file. A real rename would
        // rewrite the metadata; the alpha has no per-file metadata store, so the title change is
        // recorded as a .title file the library reads when it builds the list.
        try
        {
            var sidecar = target + ".title";
            File.WriteAllText(sidecar, parameters.Title ?? string.Empty);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Tript.App: could not rename '{target}': {exception.Message}");
        }
    }

    private string? ResolveContentFile(string fileName)
    {
        // File names are safe by construction in the alpha (the recording names the host builds);
        // still, the same traversal discipline applies — resolve against the root and refuse
        // anything that escapes it.
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

        var session = _sessionTracker.Active;
        if (session is not null)
        {
            // The recording is live: the bookmark goes into the session so the detector and the
            // user share one list.
            var bookmark = new Tript.Core.Bookmark
            {
                Type = ParseBookmarkType(parameters.Type),
                Time = TimeSpan.FromSeconds(parameters.Time),
            };
            session.AddBookmark(bookmark);
            return;
        }

        // A finished recording: the bookmark is written to the metadata sidecar. The alpha has no
        // per-file metadata store beyond RecordingMetadata; the bookmark is appended to the file's
        // .bookmarks.json sidecar.
        var target = ResolveContentFile(parameters.FilePath);
        if (target is null)
            return;

        var sidecar = target + ".bookmarks.json";
        var bookmarks = new List<BookmarkItem>();
        if (File.Exists(sidecar))
        {
            try
            {
                bookmarks = JsonSerializer.Deserialize<List<BookmarkItem>>(File.ReadAllText(sidecar), Wire.Options) ?? [];
            }
            catch (JsonException)
            {
                bookmarks = [];
            }
        }

        bookmarks.Add(new BookmarkItem
        {
            Id = string.IsNullOrEmpty(parameters.Id) ? Guid.NewGuid().ToString("N") : parameters.Id,
            Type = parameters.Type,
            Time = parameters.Time,
        });

        try
        {
            File.WriteAllText(sidecar, JsonSerializer.Serialize(bookmarks, Wire.Options));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Tript.App: could not write bookmark sidecar: {exception.Message}");
        }
    }

    internal void DeleteBookmark(DeleteBookmarkParameters? parameters)
    {
        if (parameters is null || string.IsNullOrEmpty(parameters.FilePath))
            return;

        var target = ResolveContentFile(parameters.FilePath);
        if (target is null)
            return;

        var sidecar = target + ".bookmarks.json";
        if (!File.Exists(sidecar))
            return;

        try
        {
            var bookmarks = JsonSerializer.Deserialize<List<BookmarkItem>>(File.ReadAllText(sidecar), Wire.Options) ?? [];
            bookmarks.RemoveAll(b => b.Id == parameters.Id);
            File.WriteAllText(sidecar, JsonSerializer.Serialize(bookmarks, Wire.Options));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"Tript.App: could not write bookmark sidecar: {exception.Message}");
        }
    }

    private static Tript.Core.BookmarkType ParseBookmarkType(string type)
    {
        return Enum.TryParse<Tript.Core.BookmarkType>(type, ignoreCase: true, out var parsed)
            ? parsed
            : Tript.Core.BookmarkType.Manual;
    }

    // ---- recovery ----

    // The minimal orphan recovery: scan the content root for files that exist on disk but have no
    // entry in the library list (a crashed recording leaves an .mp4.part or an .mp4 without a
    // metadata sidecar), and offer them via recoveryPrompt. The alpha does not have a full recovery
    // catalogue; it lists the orphan candidates and the frontend can confirm or decline.
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
    }

    private List<string> FindOrphanFiles()
    {
        var orphans = new List<string>();
        var root = new DirectoryInfo(_options.ContentRoot);
        if (!root.Exists)
            return orphans;

        foreach (var file in root.EnumerateFiles("*.mp4", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(_options.ContentRoot, file.FullName);
            var normalized = relative.Replace(Path.DirectorySeparatorChar, '/');

            // A file is orphaned when it is not part of the catalogue the library builds. The
            // catalogue has no metadata store yet; for the alpha, "in the catalogue" means the
            // file is a session path the host would have built. A file in clips/ is never orphaned.
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
        // The alpha's recovery is a no-op for "keep": the file already stays. A "delete" action
        // would remove the orphan; the frontend sends the prompt's id back so the host could
        // correlate, but the alpha has no per-file state to act on beyond deletion.
        if (parameters.Action.Equals("delete", StringComparison.OrdinalIgnoreCase))
        {
            // Deletion of an orphan is out of scope for the minimal recovery; the file stays.
        }
    }

    // ---- clipping ----

    internal void CreateClip(ClipRequest request)
    {
        _clipEngine ??= BuildClipEngine();

        // Never block synchronously: the engine's ffmpeg run is off the IPC thread, and progress
        // arrives as importProgress messages.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                _ipc.Broadcast("importProgress", JsonSerializer.SerializeToElement(new
                {
                    status = "importing",
                }, Wire.Options));

                var results = _clipEngine.CreateClips(request);

                _ipc.Broadcast("importProgress", JsonSerializer.SerializeToElement(new
                {
                    status = "done",
                    content = new ContentItem
                    {
                        ContentType = "clip",
                        FileName = Path.GetFileName(results[0]),
                        FilePath = Path.GetRelativePath(_options.ContentRoot, results[0]).Replace(Path.DirectorySeparatorChar, '/'),
                    },
                }, Wire.Options));
            }
            catch (Exception exception)
            {
                _ipc.Broadcast("importProgress", JsonSerializer.SerializeToElement(new
                {
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
        if (_recorder is not null)
            return;

        if (_options.FakeRecorder)
        {
            _recorderSession = new FakeRecorderSession();
            _recorder = new RecorderStateMachine(_recorderSession, settings);
            return;
        }

        if (_runtime is null)
            throw new InvalidOperationException("The real recorder needs a libobs runtime; none was started.");

        _colourSource = ObsSource.CreatePrivate("color_source", "app colour");
        _recorderSession = new ObsRecorderSession(_runtime, _colourSource);
        _recorder = new RecorderStateMachine(_recorderSession, settings);
    }

    private string BuildOutputPath(SettingsModel settings)
    {
        var directory = Path.Combine(_options.ContentRoot, "sessions",
            DateTime.Now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(directory);
        var name = $"session-{DateTime.Now:yyyyMMdd-HHmmss}.mp4";
        return Path.Combine(directory, name);
    }
}
