// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Detection;
using Tript.Obs;
using Serilog;

namespace Tript.Recorder;

// The detection-to-bookmarking host. Given a game, it starts a visual event detector for that game
// and turns the detections that detector raises into bookmarks on the active recording via a
// CooldownTracker.
public sealed class DetectionHost : IDisposable
{
    private readonly IVisualEventDetector _detector;
    private readonly ITrackDefinitionSource _definitionSource;
    private readonly TimeSpan _cleanupInterval;
    private readonly Lock _gate = new();

    // Serialises Start/Stop/Dispose against each other so the cleanup thread can be joined without
    // _gate held — the cleanup thread takes _gate every cycle, so joining under it is a guaranteed
    // two-second stall. Always taken before _gate, never by the cleanup thread.
    private readonly Lock _lifecycleGate = new();

    // The detector raises detections on its own thread, and Stop disposes the subscription that
    // handler is reading through, so a handler may be in flight while the host changes state. Every
    // piece of state the handler touches is swapped under a lock, and the handler reads the
    // swapped-out values through its own captured references.
    private DetectionRun? _run;
    private CleanupWorker? _cleanup;

    public DetectionHost(IVisualEventDetector detector, ITrackDefinitionSource? definitionSource = null,
        TimeSpan? cleanupInterval = null)
    {
        ArgumentNullException.ThrowIfNull(detector);
        _detector = detector;
        _definitionSource = definitionSource ?? new ModelServiceDefinitionSource();
        _cleanupInterval = cleanupInterval ?? TimeSpan.FromSeconds(1);
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
            CleanupWorker? stopped;
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

                stopped = StopLocked();
            }

            stopped?.Join();

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
            CleanupWorker? stopped;
            lock (_gate)
            {
                stopped = StopLocked();
            }

            stopped?.Join();
        }
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            CleanupWorker? stopped;
            lock (_gate)
            {
                stopped = StopLocked();
            }

            stopped?.Join();
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

        // The detector is told about a live game with a model; a failure to actually start it
        // (model file missing, ORT refusing the session) is reported up and the run is not armed.
        _detector.Start(gameId);

        var run = new DetectionRun(gameId, byClass);
        _run = run;
        _detector.DetectionsAvailable += run.OnDetections;

        if (_cleanupInterval > TimeSpan.Zero)
            ArmCleanupTimer();

        Log.Information("DetectionHost: detection started for {GameId} with {DefinitionCount} event definitions",
            gameId, definitions.Count);
        return true;
    }

    // Cancels the cleanup thread but does not join it: the caller does that with _gate released,
    // because the cleanup thread takes _gate. Null when nothing was running.
    private CleanupWorker? StopLocked()
    {
        var run = _run;
        if (run == null)
        {
            return null;
        }

        _run = null;
        _detector.DetectionsAvailable -= run.OnDetections;

        var cleanup = _cleanup;
        _cleanup = null;
        cleanup?.Cancel();

        _detector.Stop();

        Log.Information("DetectionHost: detection stopped for {GameId}", run.GameId);
        return cleanup;
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

    private void ArmCleanupTimer()
    {
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        var thread = new Thread(() =>
        {
            // Synchronously, like the detector's loop: a long-ish cleanup pass must not park on the
            // thread pool behind normal-priority detection work.
            while (!token.IsCancellationRequested)
            {
                token.WaitHandle.WaitOne(_cleanupInterval);
                if (token.IsCancellationRequested)
                    break;

                DetectionRun? run;
                lock (_gate)
                {
                    run = _run;
                }

                if (run != null)
                {
                    try
                    {
                        run.Tracker.Cleanup(DateTime.Now);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "DetectionHost: cooldown cleanup failed");
                    }
                }
            }
        })
        {
            IsBackground = true,
            Name = "Tript.DetectionHost.Cleanup",
        };

        _cleanup = new CleanupWorker(thread, cts);
        thread.Start();
    }

    // The cleanup thread and the token that stops it. Cancel is safe under _gate; Join must not be,
    // because the thread it waits for takes _gate itself. The token source is disposed only after a
    // successful join — its kernel wait handle is what the loop parks on, and disposing it while the
    // thread is still there turns a stall into an exception.
    private sealed class CleanupWorker(Thread thread, CancellationTokenSource cts)
    {
        internal void Cancel() => cts.Cancel();

        internal void Join()
        {
            if (thread == Thread.CurrentThread)
                return;

            if (thread.Join(TimeSpan.FromSeconds(2)))
                cts.Dispose();
            else
                Log.Warning("DetectionHost: cleanup thread did not exit within 2s");
        }
    }

    // The per-game state a detector's detections are routed through. Instances are immutable once
    // created, so a handler already running when the host swaps the run out keeps reading the run
    // it captured and finishes without touching the new run's state.
    private sealed class DetectionRun
    {
        internal string GameId { get; }

        internal CooldownTracker Tracker { get; }

        private readonly IReadOnlyDictionary<int, EventDefinition> _definitionsByClass;
        private readonly IReadOnlyDictionary<int, EventDefinition> _definitionsById;

        internal DetectionRun(string gameId, IReadOnlyDictionary<int, EventDefinition> definitionsByClass)
        {
            GameId = gameId;
            _definitionsByClass = definitionsByClass;
            _definitionsById = definitionsByClass.Values
                .GroupBy(definition => definition.Id)
                .ToDictionary(group => group.Key, group => group.First());
            Tracker = new CooldownTracker();
        }

        internal void OnDetections(List<DetectionResult> detections)
        {
            if (detections == null || detections.Count == 0)
                return;

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

            // Exclusions are modelled as vetoes for the complete inference cycle: a kill-feed icon
            // seen alongside a kill-cam/death-spectating icon is ambiguous and must not create a
            // bookmark. Do this before the cooldown tracker so suppressed triggers do not leave
            // active instances that could affect a later, unexcluded cycle.
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
                    + CooldownTracker.CountDistinctDetections(
                        subtractorGroup.Select(item => item.Detection).ToList());
            }

            foreach (var (detection, definition) in resolved)
            {
                if (definition.Type != EventType.Trigger)
                    continue;

                subtractionCounts.TryGetValue(definition.Id, out var remainingSubtractions);
                var created = Tracker.ProcessDetection(detection, definition, now,
                    createBookmark: remainingSubtractions <= 0);
                if (created && remainingSubtractions > 0)
                    subtractionCounts[definition.Id] = remainingSubtractions - 1;
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
