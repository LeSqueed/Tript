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

    // The primary display's resolution, detected once at startup (Program.BuildApp), or null when
    // this machine would not say. Offered to the settings UI on every settings push; never
    // persisted, because it is a fact about the machine rather than a setting.
    private readonly DisplaySize? _primaryDisplay;

    private readonly AppController _controller;
    private readonly IpcServer _ipc;
    private readonly ContentServer _content;
    private readonly UiHost _ui;
    private readonly RecordingMetadataStore _metadata;
    private readonly ClipTitleStore _clipTitles;

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

    // primaryDisplay is optional: a host constructed without one (the folder-picker tests) simply
    // pushes no display resolution, and the settings UI offers its preset list alone.
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
        _ipc = new IpcServer(_controller);
        _content = new ContentServer(EffectiveRoot);
        _ui = new UiHost(options.WebRoot);
        _metadata = new RecordingMetadataStore(Path.Combine(EffectiveRoot, "metadata"));
        _clipTitles = new ClipTitleStore(Path.Combine(EffectiveRoot, "metadata"));

        Directory.CreateDirectory(EffectiveRoot);
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

    // The single root everything content lives under: sessions, clips, the metadata tree and the
    // content server's traversal guard all resolve against it. A configured
    // Recording.OutputDirectory is the effective root; empty falls back to the content root. A
    // settings change that moves OutputDirectory updates it in place.
    internal string EffectiveRoot { get; private set; }

    private static string ResolveEffectiveRoot(AppOptions options, SettingsStore settingsStore)
    {
        var configured = settingsStore.Load().Recording.OutputDirectory;
        return string.IsNullOrWhiteSpace(configured) ? options.ContentRoot : configured;
    }

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
            WriteMetadataRecord(_pendingMetadata);
        }

        _pendingMetadata = null;
        _activeOutputPath = null;
        _currentGameId = null;

        PushState(recording: false, null);
        return true;
    }

    // Persists the recording's metadata — game, start time, content type, audio tracks, the
    // automatic bookmarks the detection host produced and the link key back to the video — into
    // the metadata store (spec/config-and-storage.md), so bookmarks survive the process. The
    // record is written only when the recording actually exists. A session is now part of the
    // library, so the content list is pushed. The auto bookmarks are best-effort: a failed write
    // is logged (inside the store) but does not abort the stop or push an error — the record the
    // session built is still offered to the library.
    private void WriteMetadataRecord(RecordingMetadata metadata)
    {
        if (_activeOutputPath is null || !File.Exists(_activeOutputPath))
            return;

        var relative = Path.GetRelativePath(EffectiveRoot, _activeOutputPath)
            .Replace(Path.DirectorySeparatorChar, '/');
        metadata.VideoPath = relative;

        _metadata.Save(metadata);
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

    // The seam the desktop shell installs: a function that opens a native folder picker and returns
    // the chosen directory, or null when the user cancels. The headless host has no window, so the
    // delegate stays null there and RequestVideoLocation is a documented no-op. The shell installs
    // it after the Photino window is created (a WindowCreated handler), so it is never set before
    // the native window exists.
    internal Func<string?>? FolderPicker { get; set; }

    // The SetVideoLocation command. Invokes the shell's native picker (when one is installed) and
    // applies the picked directory as the recording output directory through the normal settings
    // path, so the field updates exactly as if the user had typed it: the settings file is saved
    // and a settings push tells every client the new value. Cancelling the picker is a no-op.
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
            // A picker failure (no native dialog, a refused GTK loop) must not crash the host or
            // tear down the IPC channel; the user simply stays where they were.
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

        // A changed OutputDirectory takes effect immediately, without a restart: the effective
        // root, the content server's guard root and the metadata store are rebuilt from the
        // updated settings.
        var effectiveRoot = Path.GetFullPath(ResolveEffectiveRoot(_options, _settingsStore));
        if (!string.Equals(effectiveRoot, EffectiveRoot, StringComparison.Ordinal))
        {
            EffectiveRoot = effectiveRoot;
            _content.UpdateRoot(effectiveRoot);
            _metadata.UpdateRoot(Path.Combine(effectiveRoot, "metadata"));
            _clipTitles.UpdateRoot(Path.Combine(effectiveRoot, "metadata"));
            Directory.CreateDirectory(effectiveRoot);
        }

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
            // The encoder list is the machine's actual H.264 set, settled by the runtime. It can
            // only be probed when libobs is loaded — the fake-recorder host never starts it, so a
            // P/Invoke there would segfault rather than answer — so the list is absent (null) and
            // the frontend falls back to the current value plus obs_x264.
            availableEncoders = _runtime is null ? null : ObsRecorderSession.EnumerateUsableEncoderIds(),
            // The primary display's resolution, so the resolution selector can offer this machine's
            // own size. A sibling of `settings` for the same reason the encoder list is one: it is a
            // property of the machine, not a persisted setting, and RecordingSettings carries
            // JsonExtensionData — a field nested under `recording` would be round-tripped straight
            // into the settings file on the next save. Null when detection failed, and the frontend
            // then offers the presets alone.
            displayResolution = _primaryDisplay is { IsUsable: true } display
                ? (object?)new { width = display.Width, height = display.Height }
                : null,
        }, Wire.Options));
    }

    internal void PushGameList()
    {
        var element = JsonSerializer.SerializeToElement(GameList, Wire.Options);
        _ipc.Broadcast("gameList", element);
    }

    // ---- content ----

    // The path every content URL is resolved against. ContentServer holds the same root (it owns
    // the traversal guard), so this is kept for callers that need the root without the server.
    internal string ContentRoot => EffectiveRoot;

    // Rebuilds the content catalogue from disk and broadcasts it as the "content" message. The
    // frontend requests it on mount (the ListContent command) and the host pushes it whenever the
    // catalogue changes — after a recording stops, a clip completes, a rename, or a delete.
    internal void PushContent()
    {
        _ipc.Broadcast("content", JsonSerializer.SerializeToElement(new
        {
            content = ListContent(),
        }, Wire.Options));
    }

    // Surfaces a metadata write failure to the user. A bookmark or title that failed to persist
    // must not silently vanish: this broadcasts the error so the frontend can show it (and keep
    // the in-memory change out of the list). The 'error' method is a backend -> frontend message;
    // the frontend registers it the same way it registers state/content pushes.
    private void PushMetadataSaveError(string message)
    {
        _ipc.Broadcast("error", JsonSerializer.SerializeToElement(new
        {
            message,
        }, Wire.Options));
    }

    internal List<ContentItem> ListContent()
    {
        var items = new List<ContentItem>();
        var root = new DirectoryInfo(EffectiveRoot);
        if (!root.Exists)
            return items;

        foreach (var file in root.EnumerateFiles("*.mp4", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(EffectiveRoot, file.FullName)
                .Replace(Path.DirectorySeparatorChar, '/');
            var topLevel = TopLevelDirectory(relative);
            var contentType = topLevel.Equals("clips", StringComparison.Ordinal) ? "clip" : "recording";

            var item = new ContentItem
            {
                ContentType = contentType,
                FileName = file.Name,
                FilePath = relative,
                Title = Path.GetFileNameWithoutExtension(file.Name),
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
                    item.StartTime = DateTimeToUnixSeconds(metadata.StartTime);
                }
                else
                {
                    // A session with no metadata record still lists — empty bookmarks, no title.
                    item.Bookmarks = [];
                }
            }
            else
            {
                // A clip with a stored user title shows it; a clip without one falls back to its
                // file-name-without-extension, exactly like a session with no metadata record.
                var title = _clipTitles.Load(file.Name);
                if (!string.IsNullOrWhiteSpace(title))
                    item.Title = title;
            }

            items.Add(item);
        }

        return items;
    }

    // The first path segment of a '/' separated relative path. Used to classify an item by its
    // top-level directory (sessions/ -> recording, clips/ -> clip).
    private static string TopLevelDirectory(string relativePath)
    {
        var separator = relativePath.IndexOf('/');
        return separator >= 0 ? relativePath[..separator] : relativePath;
    }

    private static double? DateTimeToUnixSeconds(DateTime dateTime)
        => dateTime == default ? null : new DateTimeOffset(dateTime).ToUnixTimeSeconds();

    // ---- content operations ----

    internal void DeleteContent(DeleteContentParameters? parameters)
    {
        if (parameters is null || string.IsNullOrEmpty(parameters.FileName))
            return;

        // Resolve against the root even when the file is missing: a delete for a video whose file
        // was removed out-of-band must still drop the metadata record (cascade-delete contract).
        // ResolveContentFile refuses paths with no file on disk, so use the traversal-safe
        // resolver directly and let File.Delete be the no-op it already is.
        var target = _content.ResolveWithinRoot(parameters.FileName);
        if (target is null)
            return;

        try
        {
            File.Delete(target);
            // The cascade-delete contract: a deleted video takes its metadata records with it, so
            // the metadata/ tree never keeps an orphaned record for a video that is gone. Clips
            // have no RecordingMetadata record, but a clip's title record is deleted the same way.
            _metadata.Delete(Path.GetFileName(target));
            _clipTitles.Delete(Path.GetFileName(target));
            PushContent();
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

        // The title is stored on the video's metadata record; a video with no record yet gets one
        // (the record is created with just the link key and the title). The library reads the
        // title back when it builds the list.
        var fileName = Path.GetFileName(target);
        var metadata = _metadata.Load(fileName) ?? new RecordingMetadata
        {
            VideoPath = Path.GetRelativePath(EffectiveRoot, target).Replace(Path.DirectorySeparatorChar, '/'),
        };
        metadata.Title = parameters.Title;

        if (!_metadata.Save(metadata))
        {
            // A title that could not be written must not silently vanish: surface the failure to
            // the user and leave the library list as it was — no content push, so the old title
            // stays on screen.
            PushMetadataSaveError("The recording title could not be saved — check the recording folder is writable.");
            return;
        }
        PushContent();
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

        // A finished recording: the bookmark is appended to the video's metadata record in the
        // metadata store. A video with no record yet gets one (just the link key and the
        // bookmark).
        var target = ResolveContentFile(parameters.FilePath);
        if (target is null)
            return;

        var fileName = Path.GetFileName(target);
        var metadata = _metadata.Load(fileName) ?? new RecordingMetadata
        {
            VideoPath = Path.GetRelativePath(EffectiveRoot, target).Replace(Path.DirectorySeparatorChar, '/'),
        };
        metadata.Bookmarks.Add(new Tript.Core.Bookmark
        {
            // The frontend may send its own id or none at all; the store keys bookmarks by a
            // GUID, so an unparseable or absent id gets a fresh one.
            Id = Guid.TryParse(parameters.Id, out var parsedId) ? parsedId : Guid.NewGuid(),
            Type = ParseBookmarkType(parameters.Type),
            Time = TimeSpan.FromSeconds(parameters.Time),
        });

        if (!_metadata.Save(metadata))
        {
            // A bookmark that could not be written must not silently vanish: the frontend needs
            // to know the add failed so it does not keep the bookmark in the UI.
            PushMetadataSaveError("The bookmark could not be saved — check the recording folder is writable.");
        }
    }

    internal void DeleteBookmark(DeleteBookmarkParameters? parameters)
    {
        if (parameters is null || string.IsNullOrEmpty(parameters.FilePath))
            return;

        var target = ResolveContentFile(parameters.FilePath);
        if (target is null)
            return;

        var fileName = Path.GetFileName(target);
        var metadata = _metadata.Load(fileName);
        if (metadata is null)
            return;

        var id = Guid.TryParse(parameters.Id, out var parsedId) ? parsedId : Guid.Empty;
        metadata.Bookmarks.RemoveAll(b => b.Id == id);
        if (!_metadata.Save(metadata))
        {
            // A bookmark whose removal could not be persisted must not silently reappear on the
            // next list build: the frontend needs to know the delete failed.
            PushMetadataSaveError("The bookmark could not be removed — check the recording folder is writable.");
        }
    }

    private static Tript.Core.BookmarkType ParseBookmarkType(string type)
    {
        return Enum.TryParse<Tript.Core.BookmarkType>(type, ignoreCase: true, out var parsed)
            ? parsed
            : Tript.Core.BookmarkType.Manual;
    }

    // ---- recovery ----

    // The minimal orphan recovery: scan the recording root for files that exist on disk but have no
    // entry in the library list (a crashed recording leaves an .mp4.part or an .mp4 without a
    // metadata record), and offer them via recoveryPrompt. The alpha does not have a full recovery
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
        var root = new DirectoryInfo(EffectiveRoot);
        if (!root.Exists)
            return orphans;

        foreach (var file in root.EnumerateFiles("*.mp4", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(EffectiveRoot, file.FullName);
            var normalized = relative.Replace(Path.DirectorySeparatorChar, '/');

            // A file is orphaned when it is not part of the catalogue the library builds — the
            // session paths the host would have built. A file in clips/ is never orphaned.
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

    // Reports a clip that could not even be started — a source path that does not resolve inside the
    // recording root (AppController.CreateClip). It uses the same importProgress "error" frame the
    // engine's failures below use, because that is the message the clip dialog already renders its
    // failure state from; a refusal must not look like a clip that silently never happened.
    internal void PushClipError(string message)
    {
        _ipc.Broadcast("importProgress", JsonSerializer.SerializeToElement(new
        {
            status = "error",
            error = message,
        }, Wire.Options));
    }

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

                // The user's clip title is persisted against every produced file (one in combine
                // mode, one per region in separate mode), so the clips list shows it across
                // restarts. A failed write is logged inside the store and does not fail the clip;
                // the clip still lists, just under its file name.
                if (!string.IsNullOrWhiteSpace(request.Title))
                {
                    foreach (var result in results)
                        _clipTitles.Save(Path.GetFileName(result), request.Title);
                }

                _ipc.Broadcast("importProgress", JsonSerializer.SerializeToElement(new
                {
                    status = "done",
                    content = new ContentItem
                    {
                        ContentType = "clip",
                        FileName = Path.GetFileName(results[0]),
                        FilePath = Path.GetRelativePath(EffectiveRoot, results[0]).Replace(Path.DirectorySeparatorChar, '/'),
                        Title = string.IsNullOrWhiteSpace(request.Title) ? null : request.Title,
                    },
                }, Wire.Options));

                // A clip completed: the catalogue changed, so the content list is pushed.
                PushContent();
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

    // The output path for a recording. Sessions are flat under <effectiveRoot>/sessions/ — the
    // timestamp is already in the file name, so there is no date subfolder. The sessions directory
    // is created unconditionally, as before.
    private string BuildOutputPath(SettingsModel settings)
    {
        var directory = Path.Combine(EffectiveRoot, "sessions");
        Directory.CreateDirectory(directory);
        // Millisecond resolution keeps two sessions started in the same second from colliding on
        // one file name (a stop/start in quick succession would otherwise overwrite the recording
        // and its metadata record).
        var name = $"session-{DateTime.Now:yyyyMMdd-HHmmssfff}.mp4";
        return Path.Combine(directory, name);
    }
}
