// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Detection;
using Tript.Obs;
using Serilog;

namespace Tript.Recorder;

// The detection-to-bookmarking host. Given a game, it starts a visual event detector for that game
// and turns the detections that detector raises into bookmarks on the active recording via a
// CooldownTracker. It owns nothing else: the recorder (its caller) decides *when* a game is active
// and *when* a recording is in progress; this host decides how a game's detections become bookmarks.
//
// The lifecycle of one game:
//
//   Start(gameId)
//     ensure a frame source is registered   (fail early with a clear message, not a throw deep
//                                           inside the detector's thread)
//     guard on ModelService.HasModelForGame  (a supported game with no model is reported, not crashed)
//     load the game's EventDefinitions once  (keyed by ClassId, events.json already does this)
//     start the detector for the game
//     subscribe to DetectionsAvailable
//     arm the cooldown cleanup timer
//   detections -> for each, the definition with a matching ClassId -> CooldownTracker.ProcessDetection
//     + Cleanup on an interval so a stale instance is retired rather than swallowing the next event
//   Stop()
//     stop the detector, unsubscribe, stop the cleanup timer
//
// Definitions are keyed by ClassId: events.json already keys them by class (the detection models
// ship hand-in-hand with events.json, and the detector itself cross-checks the two at Start). The
// matching is a simple dictionary lookup; the detector has already decided which class a box is.
//
// A definition without a BookmarkType is "detected but never bookmarked" by design (spec,
// training.md): exclusion definitions suppress, they do not bookmark. The CooldownTracker already
// implements that rule; the host's only job is to hand it the right definition.
//
// The host is deliberately not coupled to the recorder's control flow: it exposes Start/Stop and
// the observation points the recorder needs, and nothing else.
public sealed class DetectionHost : IDisposable
{
    private readonly IVisualEventDetector _detector;
    private readonly ITrackDefinitionSource _definitionSource;
    private readonly TimeSpan _cleanupInterval;
    private readonly Lock _gate = new();

    // The detector raises detections on its own thread, and Stop disposes the subscription that
    // handler is reading through, so a handler may be in flight while the host changes state. Every
    // piece of state the handler touches is swapped under a lock, and the handler reads the
    // swapped-out values through its own captured references.
    private DetectionRun? _run;
    private CancellationTokenSource? _cleanupCts;
    private Thread? _cleanupThread;

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

        lock (_gate)
        {
            if (_run != null)
            {
                if (_run.GameId == gameId)
                {
                    Log.Information("DetectionHost: detection already running for {GameId}", gameId);
                    return true;
                }

                Log.Information("DetectionHost: game switch {OldGameId} -> {NewGameId}",
                    _run.GameId, gameId);
                StopLocked();
            }

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

    // Stops detection for the current game, if any. No-op when nothing is running.
    public void Stop()
    {
        lock (_gate)
        {
            StopLocked();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            StopLocked();
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

    private void StopLocked()
    {
        var run = _run;
        if (run == null)
        {
            return;
        }

        _run = null;
        _detector.DetectionsAvailable -= run.OnDetections;
        StopCleanupTimer();
        _detector.Stop();

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

    private void ArmCleanupTimer()
    {
        var cts = new CancellationTokenSource();
        _cleanupCts?.Cancel();
        _cleanupCts = cts;

        var token = cts.Token;
        _cleanupThread = new Thread(() =>
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
        _cleanupThread.Start();
    }

    private void StopCleanupTimer()
    {
        _cleanupCts?.Cancel();
        var thread = _cleanupThread;
        if (thread != null && thread != Thread.CurrentThread)
        {
            if (!thread.Join(TimeSpan.FromSeconds(2)))
            {
                Log.Warning("DetectionHost: cleanup thread did not exit within 2s");
            }
        }

        _cleanupCts = null;
        _cleanupThread = null;
    }

    // The per-game state a detector's detections are routed through. Instances are immutable once
    // created, so a handler already running when the host swaps the run out keeps reading the run
    // it captured and finishes without touching the new run's state.
    private sealed class DetectionRun
    {
        internal string GameId { get; }

        internal CooldownTracker Tracker { get; }

        private readonly IReadOnlyDictionary<int, EventDefinition> _definitionsByClass;

        internal DetectionRun(string gameId, IReadOnlyDictionary<int, EventDefinition> definitionsByClass)
        {
            GameId = gameId;
            _definitionsByClass = definitionsByClass;
            Tracker = new CooldownTracker();
        }

        internal void OnDetections(List<DetectionResult> detections)
        {
            if (detections == null || detections.Count == 0)
                return;

            var now = DateTime.Now;
            foreach (var detection in detections)
            {
                if (!_definitionsByClass.TryGetValue(detection.ClassId, out var definition))
                {
                    Log.Debug("DetectionHost: no event definition for class {ClassId} of {GameId}; dropping",
                        detection.ClassId, GameId);
                    continue;
                }

                Tracker.ProcessDetection(detection, definition, now);
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
