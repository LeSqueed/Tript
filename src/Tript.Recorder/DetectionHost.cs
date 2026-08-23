// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Detection;
using Tript.Obs;
using Tript.Core;
using Serilog;

namespace Tript.Recorder;

// The detection-to-bookmarking host. Given a game, it starts a visual event detector for that game
// and turns the detections that detector raises into bookmarks on the active recording via
// frame-to-frame net-count transitions.
public sealed class DetectionHost : IDisposable
{
    private readonly IVisualEventDetector _detector;
    private readonly ITrackDefinitionSource _definitionSource;
    private readonly Action<Bookmark>? _onAutomaticClipBookmark;
    private readonly Lock _gate = new();

    // Serialises Start/Stop/Dispose against each other.
    private readonly Lock _lifecycleGate = new();

    // The detector raises detections on its own thread, and Stop disposes the subscription that
    // handler is reading through, so a handler may be in flight while the host changes state. Every
    // piece of state the handler touches is swapped under a lock, and the handler reads the
    // swapped-out values through its own captured references.
    private DetectionRun? _run;
    public DetectionHost(IVisualEventDetector detector, ITrackDefinitionSource? definitionSource = null,
        Action<Bookmark>? onAutomaticClipBookmark = null)
    {
        ArgumentNullException.ThrowIfNull(detector);
        _detector = detector;
        _definitionSource = definitionSource ?? new ModelServiceDefinitionSource();
        _onAutomaticClipBookmark = onAutomaticClipBookmark;
    }

    // Whether a detector is currently running for a game.
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _run != null;
            }
        }
    }

    // The game the running detector was started for; null when nothing is running.
    public string? CurrentGameId
    {
        get
        {
            lock (_gate)
            {
                return _run?.GameId;
            }
        }
    }

    // The game the recorder last decided to detect. Set once a detector actually starts for it, and
    // cleared by Stop — Start keeps its game when a later Start switches games, so the recorder can
    // tell "I asked for X" from "X is live".
    public string? LastStartedGameId
    {
        get
        {
            lock (_gate)
            {
                return _run?.GameId;
            }
        }
    }

    // Starts detection for the given game. A game already running is left running and reports true;
    // a different game stops the old detector and starts the new. A game with no model is refused
    // (false) rather than thrown at.
    public bool Start(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        lock (_lifecycleGate)
        {
            lock (_gate)
            {
                if (_run != null && _run.GameId == gameId)
                {
                    Log.Information("DetectionHost: detection already running for {GameId}", gameId);
                    return true;
                }

                if (_run != null)
                    Log.Information("DetectionHost: game switch {OldGameId} -> {NewGameId}",
                        _run.GameId, gameId);

                StopLocked();
            }

            lock (_gate)
            {
                try
                {
                    return StartLocked(gameId);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "DetectionHost: could not start detection for {GameId}", gameId);
                    return false;
                }
            }
        }
    }

    // Stops detection for the current game, if any. No-op when nothing is running.
    public void Stop()
    {
        lock (_lifecycleGate)
        {
            lock (_gate)
            {
                StopLocked();
            }
        }
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            lock (_gate)
            {
                StopLocked();
            }
            _detector.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private bool StartLocked(string gameId)
    {
        // Fail before anything else starts, with a message that says what is missing rather than
        // letting the detector throw from inside its own thread.
        if (!HasRegisteredFrameSource())
        {
            Log.Warning("DetectionHost: cannot start detection for {GameId}: no frame source is registered",
                gameId);
            return false;
        }

        if (!_definitionSource.HasModelForGame(gameId))
        {
            // Not an error: most games legitimately have no model (ModelService logs the same).
            Log.Information("DetectionHost: no model for {GameId}, detection not started", gameId);
            return false;
        }

        var definitions = _definitionSource.LoadEventDefinitions(gameId);
        var byClass = definitions.ToDictionary(d => d.ClassId);
        Log.Information("DetectionHost: loaded event definitions for {GameId}: {Definitions}",
            gameId, string.Join(", ", definitions.Select(d => $"{d.ClassId}={d.Name}/{d.BookmarkType?.ToString() ?? "none"}")));

        var run = new DetectionRun(gameId, byClass, _onAutomaticClipBookmark);
        _run = run;
        _detector.DetectionsAvailable += run.OnDetections;

        // Subscribe before starting so a detector that emits immediately cannot lose its first
        // batch. Roll the subscription back if startup itself fails.
        try
        {
            _detector.Start(gameId);
        }
        catch
        {
            _detector.DetectionsAvailable -= run.OnDetections;
            _run = null;
            throw;
        }

        Log.Information("DetectionHost: detection started for {GameId} with {DefinitionCount} event definitions",
            gameId, definitions.Count);
        return true;
    }

    private void StopLocked()
    {
        var run = _run;
        if (run == null) return;

        run.BeginStop();
        _run = null;
        _detector.DetectionsAvailable -= run.OnDetections;

        _detector.Stop();
        run.WaitForCallbacks();

        Log.Information("DetectionHost: detection stopped for {GameId}", run.GameId);
    }

    private bool HasRegisteredFrameSource()
    {
        try
        {
            return FrameSourceRegistry.Current is not null;
        }
        catch (InvalidOperationException)
        {
            // The registry's contract: Current throws when nothing is registered.
            return false;
        }
    }

    // The per-game state a detector's detections are routed through. A handler already running when
    // the host swaps the run out keeps reading the run it captured and finishes without touching the
    // new run's state.
    private sealed class DetectionRun
    {
        internal string GameId { get; }

        private readonly IReadOnlyDictionary<int, EventDefinition> _definitionsByClass;
        private readonly IReadOnlyDictionary<int, EventDefinition> _definitionsById;
        private readonly Action<Bookmark>? _onAutomaticClipBookmark;
        private readonly object _processingGate = new();
        private readonly Dictionary<int, int> _previousNetCounts = new();
        private readonly object _callbackGate = new();
        private int _callbacksInFlight;
        private bool _stopping;

        internal DetectionRun(string gameId, IReadOnlyDictionary<int, EventDefinition> definitionsByClass,
            Action<Bookmark>? onAutomaticClipBookmark)
        {
            GameId = gameId;
            _definitionsByClass = definitionsByClass;
            _onAutomaticClipBookmark = onAutomaticClipBookmark;
            _definitionsById = definitionsByClass.Values
                .GroupBy(definition => definition.Id)
                .ToDictionary(group => group.Key, group => group.First());
        }

        internal void OnDetections(List<DetectionResult> detections)
        {
            lock (_callbackGate)
            {
                if (_stopping)
                    return;

                _callbacksInFlight++;
            }

            try
            {
                lock (_processingGate)
                    ProcessDetections(detections ?? []);
            }
            finally
            {
                lock (_callbackGate)
                {
                    _callbacksInFlight--;
                    if (_callbacksInFlight == 0)
                        Monitor.PulseAll(_callbackGate);
                }
            }
        }

        private void ProcessDetections(IReadOnlyList<DetectionResult> detections)
        {
            var now = DateTime.Now;
            Log.Information("DetectionHost: received {Count} detection(s) for {GameId}: classes {Classes}",
                detections.Count, GameId, string.Join(",", detections.Select(d => d.ClassId).Distinct()));

            var resolved = new List<(DetectionResult Detection, EventDefinition Definition)>();
            foreach (var detection in detections)
            {
                if (!_definitionsByClass.TryGetValue(detection.ClassId, out var definition))
                {
                    Log.Warning("DetectionHost: no event definition for class {ClassId} of {GameId}; dropping",
                        detection.ClassId, GameId);
                    continue;
                }

                resolved.Add((detection, definition));
            }

            // An exclusion vetoes the complete cycle. Do not update the previous counts: this frame
            // is not an effective observation and must not make an unchanged trigger rise again.
            if (resolved.Any(item => item.Definition.Type == EventType.Exclusion))
            {
                Log.Information("DetectionHost: exclusion detected for {GameId}; suppressing {Count} event(s) in this cycle",
                    GameId, resolved.Count);
                return;
            }

            var subtractionCounts = new Dictionary<int, int>();
            foreach (var subtractorGroup in resolved
                .Where(item => item.Definition.Type == EventType.Subtractor)
                .GroupBy(item => item.Definition.Id))
            {
                var subtractor = subtractorGroup.First().Definition;
                if (subtractor.SubtractsEventId is not int targetId
                    || !_definitionsById.TryGetValue(targetId, out var target)
                    || target.Type != EventType.Trigger)
                {
                    Log.Warning("DetectionHost: subtractor in {GameId} has an invalid target; ignoring it",
                        GameId);
                    continue;
                }

                subtractionCounts[target.Id] = subtractionCounts.GetValueOrDefault(target.Id)
                    + DetectionBatchCounter.DistinctDetections(
                        subtractorGroup.Select(item => item.Detection).ToList()).Count;
            }

            var currentNetCounts = new Dictionary<int, int>();
            foreach (var definition in _definitionsById.Values.Where(item => item.Type == EventType.Trigger))
            {
                var triggerDetections = DetectionBatchCounter.DistinctDetections(
                    resolved.Where(item => item.Definition.Id == definition.Id)
                        .Select(item => item.Detection).ToList());
                subtractionCounts.TryGetValue(definition.Id, out var subtractions);
                var netCount = Math.Max(0, triggerDetections.Count - subtractions);
                currentNetCounts[definition.Id] = netCount;

                _previousNetCounts.TryGetValue(definition.Id, out var previousNetCount);
                var increase = netCount - previousNetCount;
                if (increase > 0)
                    CreateBookmarks(definition, triggerDetections, increase, now);
            }

            _previousNetCounts.Clear();
            foreach (var (eventId, count) in currentNetCounts)
                _previousNetCounts[eventId] = count;
        }

        private void CreateBookmarks(EventDefinition definition,
            IReadOnlyList<DetectionResult> detections, int count, DateTime now)
        {
            if (definition.BookmarkType is not BookmarkType bookmarkType)
                return;

            var recording = RecordingSessionRegistry.Active;
            if (recording == null)
            {
                Log.Warning("CreateBookmark: detected '{EventName}' but no active recording was available",
                    definition.Name);
                return;
            }

            for (var index = 0; index < count && index < detections.Count; index++)
            {
                var bookmark = new Bookmark
                {
                    Type = bookmarkType,
                    Time = now - recording.StartTime,
                };
                recording.AddBookmark(bookmark);
                if (definition.IncludeInAutoClips)
                    _onAutomaticClipBookmark?.Invoke(bookmark);
            }

            Log.Information("CreateBookmark: created {Count} {Type} bookmark(s) for '{EventName}'",
                Math.Min(count, detections.Count), bookmarkType, definition.Name);
        }

        internal void BeginStop()
        {
            lock (_callbackGate)
                _stopping = true;
        }

        internal void WaitForCallbacks()
        {
            lock (_callbackGate)
            {
                while (_callbacksInFlight != 0)
                    Monitor.Wait(_callbackGate);
            }
        }
    }
}

// The host's view of ModelService: a tiny seam so the model/definition lookup can be faked in unit
// tests without an ONNX model or a data directory on disk.
public interface ITrackDefinitionSource
{
    bool HasModelForGame(string gameId);

    List<EventDefinition> LoadEventDefinitions(string gameId);
}

internal sealed class ModelServiceDefinitionSource : ITrackDefinitionSource
{
    public bool HasModelForGame(string gameId) => ModelService.HasModelForGame(gameId);

    public List<EventDefinition> LoadEventDefinitions(string gameId)
        => ModelService.LoadEventDefinitions(gameId);
}
