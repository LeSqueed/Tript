// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using System.Text.Json.Nodes;
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
    private readonly ThumbnailStore _thumbnails;

    // ffmpeg/ffprobe for the library surfaces (thumbnails and the duration backfill), located at
    // most once per process: FfmpegLocator.Locate walks PATH and then runs `-version` on both
    // binaries to verify them, which is four processes, and the answer cannot change while the host
    // runs. Null means the machine has no usable ffmpeg — the library then shows placeholder cards
    // and no durations, and nothing else about it changes. The clip engine keeps its own Locate call:
    // its failure is user-facing (the clip dialog shows the locator's message) rather than silent.
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

    // Files whose duration could not be read, so a broken or non-video file is probed at most once
    // per process instead of on every content push. Absolute paths.
    private readonly HashSet<string> _unprobeable = new(StringComparer.Ordinal);

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
        _metadata = new RecordingMetadataStore(Path.Combine(EffectiveRoot, "metadata"));
        _clipTitles = new ClipTitleStore(Path.Combine(EffectiveRoot, "metadata"));
        _thumbnails = new ThumbnailStore(ThumbnailRootFor(EffectiveRoot), CreateThumbnailExtractor);
        _content = new ContentServer(EffectiveRoot, _thumbnails);
        _ui = new UiHost(options.WebRoot);

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

    // The thumbnail cache sits inside the metadata tree (see ThumbnailStore for why), in its own
    // subdirectory so the record directory stays hand-readable.
    private static string ThumbnailRootFor(string effectiveRoot) =>
        Path.Combine(effectiveRoot, "metadata", "thumbnails");

    // The library's frame extractor, or null when this machine has no usable ffmpeg. Called at most
    // once, by the thumbnail store's own lazy.
    private IThumbnailExtractor? CreateThumbnailExtractor()
    {
        var tools = _libraryTools.Value;
        return tools is null ? null : new FfmpegThumbnailExtractor(tools.Value.Ffmpeg);
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

        // Point the session's game-capture source at the detected game before the recording
        // starts, so the recording shows the game rather than the colour background. The alpha
        // detector matches by process name, so the executable key is that name with the platform
        // extension; win-capture keeps retrying the hook while the source is shown, so a game
        // that appears mid-recording is still picked up.
        RetargetGameCapture(effectiveGameId);

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

        // The one write that is allowed to replace whatever is on disk, because here the in-memory
        // record is the authoritative one: this process just made the recording, and it holds the
        // game, the start time, the audio track layout and the session's bookmarks. Anything already
        // at this key belongs to a file that no longer exists (the name carries a millisecond
        // timestamp) or is the stub the library's duration probe wrote while the recording ran — so
        // there is nothing here that "unreadable" could be protecting.
        //
        // A probed duration is the exception worth carrying over: it is the one field the stub can
        // have and the session cannot know.
        if (metadata.DurationSeconds is null)
        {
            var existing = _metadata.Read(Path.GetFileName(_activeOutputPath));
            if (existing.State == StoredRecordState.Loaded)
                metadata.DurationSeconds = existing.Record!.DurationSeconds;
        }

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
            _thumbnails.UpdateRoot(ThumbnailRootFor(effectiveRoot));
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

    // The machine's active audio endpoints (WASAPI), inputs first then outputs, as the settings
    // model's device list — the specific microphones, capture devices, speakers and headsets the
    // audio page can route into a track. Each entry carries its direction (Input/Output) so the
    // frontend can label it and the routing can pick the matching capture type: an input endpoint
    // becomes a wasapi_input_capture, an output endpoint a wasapi_output_capture on that device.
    // A failure to enumerate — no endpoints, or a COM error — yields an empty list rather than
    // failing the push; the frontend then falls back to the built-in sources.
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

    private readonly HashSet<string> _detectorGameNames = [];

    internal void PushSettings()
    {
        var settings = _settingsStore.Load();
        var settingsNode = JsonSerializer.SerializeToNode(settings, SettingsSerialization.Options);
        // The audio device list is a fact about this machine, not a persisted setting — like the
        // encoder list it is settled per push, so a device unplugged after a save is not stuck in
        // the settings file. It is injected into the serialized element only; the live model (which
        // Save() would persist) is never touched.
        if (settingsNode?["audio"] is JsonObject audioNode)
            audioNode["devices"] = JsonSerializer.SerializeToNode(EnumerateAudioDevices(), SettingsSerialization.Options);
        var settingsElement = JsonSerializer.Deserialize<JsonElement>(
            settingsNode?.ToJsonString() ?? "{}", SettingsSerialization.Options);
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

    // How many previously-unseen files one call may probe for a duration. See TryReadDuration for
    // why probing happens at all; the budget is what keeps a first list of a large existing library
    // from becoming an ffprobe per item in one go. A library fills in over a few pushes and then
    // never probes those files again, in this process or a later one.
    private const int DurationProbeBudget = 12;

    // The library, rebuilt from disk. Every field the grid needs is here: the thumbnail is a URL
    // built from FilePath (the content server's /api/thumbnail route), the game, the date, the
    // duration and the size.
    //
    // The order is newest first, and it is a total order — the frontend paginates over this list, so
    // two items with the same timestamp must not be able to swap places between two pushes (and
    // EnumerateFiles' order is the file system's, not one we can rely on).
    internal List<ContentItem> ListContent()
    {
        var items = new List<ContentItem>();
        var root = new DirectoryInfo(EffectiveRoot);
        if (!root.Exists)
            return items;

        // Recording base name -> game, collected while the recordings are projected and used to give
        // the clips a game afterwards (a clip's file name starts with its source session's).
        var gamesByRecording = new Dictionary<string, string>(StringComparer.Ordinal);
        var clips = new List<ContentItem>();
        var probeBudget = DurationProbeBudget;

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
                    item.StartTime = DateTimeToUnixSeconds(metadata.StartTime);
                    item.Game = string.IsNullOrWhiteSpace(metadata.Game) ? null : metadata.Game;
                    item.DurationSeconds = metadata.DurationSeconds;

                    if (item.Game is not null)
                        gamesByRecording[Path.GetFileNameWithoutExtension(file.Name)] = item.Game;
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
                // file-name-without-extension, exactly like a session with no metadata record. The
                // clip's own record carries its duration too.
                var record = _clipTitles.LoadRecord(file.Name);
                if (!string.IsNullOrWhiteSpace(record?.Title))
                    item.Title = record.Title;
                item.DurationSeconds = record?.DurationSeconds;

                // The game is resolved after the loop: the source session may be listed after its
                // clip, so its record has not necessarily been read yet.
                clips.Add(item);
            }

            // The library shows a date on every card. A metadata record's StartTime is the
            // authoritative capture time; the file's last-write time is the fallback for content that
            // has no record — every clip, and a recording copied in by hand.
            item.StartTime ??= DateTimeToUnixSeconds(file.LastWriteTime);

            // The budget is spent on files that will actually be probed: a file already known to be
            // unreadable must not consume a slot a real recording later in the enumeration needs.
            if (item.DurationSeconds is null && probeBudget > 0 && !IsUnprobeable(file.FullName))
            {
                probeBudget--;
                item.DurationSeconds = TryReadDuration(file, relative, contentType == "recording");
            }

            items.Add(item);
        }

        foreach (var clip in clips)
            clip.Game = InheritedGame(clip.FileName, gamesByRecording);

        // Newest first, with the relative path as the tiebreak so the order is total: List.Sort is
        // unstable, and two files written in the same second would otherwise be free to swap between
        // pushes and shuffle a paginated grid under the user.
        items.Sort((left, right) =>
        {
            var byDate = (right.StartTime ?? 0).CompareTo(left.StartTime ?? 0);
            return byDate != 0 ? byDate : string.CompareOrdinal(left.FilePath, right.FilePath);
        });

        return items;
    }

    // The game a clip inherits from the session it was cut from. A clip has no metadata record of its
    // own, and nothing on the wire carries the game into CreateClip's output, so the file name is the
    // link: both clip naming paths start the name with the source session's base name —
    // AppController.BuildClipOutputPath writes "<sourceBaseName>-<clipId>.mp4" for a combine clip and
    // ClipEngine.BuildFileName writes "<sourceBaseName>-clip-<n>-<start>s-<end>s.mp4" per region in
    // separate mode.
    //
    // The longest matching session name wins, and the match must end on a '-' boundary: with sessions
    // "session-1" and "session-10" both present, "session-10-clip-x" belongs to the second, and if
    // only "session-1" has a record the boundary check stops it from claiming the other's clips.
    //
    // A clip whose source has been deleted, or whose source never had a game, simply has no game —
    // the same state as a recording with no metadata record.
    private static string? InheritedGame(string clipFileName, Dictionary<string, string> gamesByRecording)
    {
        var clipBaseName = Path.GetFileNameWithoutExtension(clipFileName);
        string? game = null;
        var matched = 0;

        foreach (var (recording, recordingGame) in gamesByRecording)
        {
            if (recording.Length <= matched)
                continue;
            if (!clipBaseName.StartsWith(recording, StringComparison.Ordinal))
                continue;
            if (clipBaseName.Length != recording.Length && clipBaseName[recording.Length] != '-')
                continue;

            game = recordingGame;
            matched = recording.Length;
        }

        return game;
    }

    // Reads a file's duration and persists it, so it is read once per file and then served from the
    // record forever after.
    //
    // Probing is the honest answer here and it is bounded rather than avoided. The alternatives were
    // weighed: StartTime/EndTime cannot supply it (StartTime is a wall-clock date, EndTime is never
    // written); writing the duration at production time is free but only ever covers content this
    // build produced, leaves every existing recording blank, and for a clip would record the
    // requested region length rather than the file's real length, which stream copy shifts by up to a
    // GOP; and MediaProbe's cache alone is per-process, so it would re-probe the whole library on
    // every start. Persisting a probed value combines the two: one ffprobe per file ever, at most
    // DurationProbeBudget of them per push, and MediaProbe's own cache absorbs repeats within the
    // process while the record absorbs them across restarts.
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
            // A text file with an .mp4 name, a truncated recording, an ffprobe that will not start:
            // the item still lists, just without a length, and it is not probed again this process.
            Console.Error.WriteLine($"Tript.App: could not read the duration of '{relativePath}': {exception.Message}");
            MarkUnprobeable(file.FullName);
            return null;
        }

        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0)
        {
            // ffprobe reports no duration at all for some containers; MediaProbe normalises that to
            // NaN. Nothing to show and nothing to store.
            MarkUnprobeable(file.FullName);
            return null;
        }

        // Persisted best-effort: a write failure (read-only media, a file where the metadata
        // directory should be) is logged inside the store and costs one probe on the next push, which
        // is not worth surfacing to the user for a duration label.
        //
        // The read-modify-write is over Read, not Load, and the difference is the whole point. Load
        // reports an unreadable record as null, which reads as "there is no record" — and this path
        // would then write a fresh record with nothing but a video path and a duration in it, over a
        // file that holds the recording's game, its user title and its bookmarks. That is a real
        // trade the wrong way round: a duration is one ffprobe away, a game and a bookmark list are
        // gone for good. So an unreadable record is left exactly as it is.
        //
        // The item still shows the duration this push — the value is measured and correct, it is only
        // not persisted — so the cost of the refusal is one probe per push for that file, bounded by
        // DurationProbeBudget. The file is deliberately not marked unprobeable: that would make the
        // length disappear from the card instead.
        if (isRecording)
        {
            var existing = _metadata.Read(file.Name);
            if (existing.MustNotBeOverwritten)
            {
                Console.Error.WriteLine(
                    $"Tript.App: '{relativePath}' has a metadata record that could not be read " +
                    $"({existing.Failure}); its duration is not persisted, so the record — the game, " +
                    "the title and the bookmarks in it — is left untouched.");
            }
            else
            {
                var metadata = existing.Record ?? new RecordingMetadata { VideoPath = relativePath };
                metadata.DurationSeconds = seconds;
                _metadata.Save(metadata);
            }
        }
        else
        {
            // The clip store makes the same distinction internally, for the same reason: a clip's
            // record carries the user's clip title.
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

    // The probe the library shares, built once. Separate from the clip engine's probe only because
    // the engine is built on demand; both are just an ffprobe path plus a per-path cache.
    private MediaProbe? LibraryProbe
    {
        get
        {
            var tools = _libraryTools.Value;
            if (tools is null)
                return null;

            // Deliberately unguarded. A clip finishing pushes content from its own thread while the
            // IPC thread may be listing, so two probes can be built; a reference assignment cannot
            // tear, MediaProbe locks its own cache, and the loser only costs a cold cache.
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
            // The file went away between the enumeration and this read; a size of 0 is better than
            // failing the whole list for it.
            return 0;
        }
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
            // have no RecordingMetadata record, but a clip's own record is deleted the same way, and
            // so is the cached thumbnail — an image left behind would both leak the deleted
            // recording's contents and be inherited by the next recording to reuse the name.
            _metadata.Delete(Path.GetFileName(target));
            _clipTitles.Delete(Path.GetFileName(target));
            _thumbnails.Delete(Path.GetFileName(target));
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
        //
        // A record that exists but could not be read is not a record to replace: writing a fresh
        // one would trade the recording's game and bookmarks for a title. The rename fails instead,
        // loudly — the user asked for this write, so they are told it did not happen.
        var fileName = Path.GetFileName(target);
        var existing = _metadata.Read(fileName);
        if (existing.MustNotBeOverwritten)
        {
            Console.Error.WriteLine(
                $"Tript.App: '{fileName}' has a metadata record that could not be read " +
                $"({existing.Failure}); the rename is refused rather than replacing it.");
            PushMetadataSaveError(
                "The recording title could not be saved — this recording's metadata record could not be read, and overwriting it would lose its game and bookmarks.");
            return;
        }

        var metadata = existing.Record ?? new RecordingMetadata
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

        // As in RenameContent: an unreadable record is preserved, not replaced. A blank record with
        // one bookmark in it would cost the recording's game, title and every bookmark already on
        // it, so the add fails and the frontend is told.
        var fileName = Path.GetFileName(target);
        var existing = _metadata.Read(fileName);
        if (existing.MustNotBeOverwritten)
        {
            Console.Error.WriteLine(
                $"Tript.App: '{fileName}' has a metadata record that could not be read " +
                $"({existing.Failure}); the bookmark is refused rather than replacing it.");
            PushMetadataSaveError(
                "The bookmark could not be saved — this recording's metadata record could not be read, and overwriting it would lose its game and existing bookmarks.");
            return;
        }

        var metadata = existing.Record ?? new RecordingMetadata
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

        // This path already refused to write when the record would not load — it returned early on
        // null — but it said nothing, so an unreadable record made a delete look like it worked and
        // the bookmark came back on the next list. The two states are now told apart: nothing to
        // delete is silence, a record that could not be read is an error the frontend must see.
        var fileName = Path.GetFileName(target);
        var existing = _metadata.Read(fileName);
        if (existing.MustNotBeOverwritten)
        {
            Console.Error.WriteLine(
                $"Tript.App: '{fileName}' has a metadata record that could not be read " +
                $"({existing.Failure}); the bookmark removal is refused rather than replacing it.");
            PushMetadataSaveError(
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

        // The colour source is the recording's background behind the game capture. The plugin's
        // default colour is white (0xFFFFFFFF) — without an explicit colour the recordings would
        // render as a white canvas until a game is hooked.
        using (var colourSettings = new ObsSettings())
        {
            colourSettings.SetInt("color", unchecked((int)0xFF000000));
            _colourSource = ObsSource.CreatePrivate("color_source", "app colour", colourSettings);
        }
        _recorderSession = new ObsRecorderSession(_runtime, _colourSource);
        _recorder = new RecorderStateMachine(_recorderSession, settings);
    }

    // Re-points the session's game-capture source at the game being recorded. The alpha detector
    // (ProcessNameGameDetector) matches by process name, so the executable key is that name with
    // the platform extension; a game id that is not a process name falls back to the same value.
    // The session no-ops when the platform has no game capture (Linux) or the session is the fake.
    private void RetargetGameCapture(string gameId)
    {
        if (_recorderSession is not ObsRecorderSession session)
            return;

        var gameName = GameList.FirstOrDefault(g => g.Id == gameId)?.Name ?? gameId;
        var executable = OperatingSystem.IsWindows() ? $"{gameName}.exe" : gameName;
        session.RetargetGame(new ObsGameCaptureTarget(null, null, executable));
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
