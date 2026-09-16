// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Detection;
using Tript.Obs;
using Tript.Core;
using Serilog;
using Serilog.Events;

namespace Tript.Recorder;

public sealed class DetectionHost : IDisposable
{
    private readonly IVisualEventDetector _detector;
    private readonly ITrackDefinitionSource _definitionSource;
    private readonly Action<Bookmark>? _onAutomaticClipBookmark;
    private readonly Lock _gate = new();

    private readonly Lock _lifecycleGate = new();

    private DetectionRun? _run;
    public DetectionHost(IVisualEventDetector detector, ITrackDefinitionSource? definitionSource = null,
        Action<Bookmark>? onAutomaticClipBookmark = null)
    {
        ArgumentNullException.ThrowIfNull(detector);
        _detector = detector;
        _definitionSource = definitionSource ?? new ModelServiceDefinitionSource();
        _onAutomaticClipBookmark = onAutomaticClipBookmark;
    }

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
        if (!HasRegisteredFrameSource())
        {
            Log.Warning("DetectionHost: cannot start detection for {GameId}: no frame source is registered",
                gameId);
            return false;
        }

        if (!_definitionSource.HasDetectionBundleForGame(gameId))
        {
            Log.Information("DetectionHost: no model for {GameId}, detection not started", gameId);
            return false;
        }

        var definitions = _definitionSource.LoadEventDefinitions(gameId);
        var byClass = definitions
            .Where(definition => definition.DetectionKind == DetectionKind.Object)
            .ToDictionary(definition => definition.ClassId);
        Log.Information("DetectionHost: loaded event definitions for {GameId}: {Definitions}",
            gameId, string.Join(", ", definitions.Select(d => $"{d.ClassId}={d.Name}/{d.BookmarkType?.ToString() ?? "none"}")));

        var run = new DetectionRun(gameId, definitions, byClass, _onAutomaticClipBookmark);
        _run = run;
        _detector.DetectionsAvailable += run.OnDetections;

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
            return false;
        }
    }

    private sealed class DetectionRun
    {
        internal string GameId { get; }

        private readonly IReadOnlyDictionary<int, EventDefinition> _definitionsByClass;
        private readonly Action<Bookmark>? _onAutomaticClipBookmark;
        private readonly object _processingGate = new();
        private readonly OcrTextTracker _ocrTracker;
        private readonly TriggerCounter _triggerCounter;
        private readonly object _callbackGate = new();
        private int _callbacksInFlight;
        private bool _stopping;

        internal DetectionRun(string gameId, IReadOnlyList<EventDefinition> definitions,
            IReadOnlyDictionary<int, EventDefinition> definitionsByClass,
            Action<Bookmark>? onAutomaticClipBookmark)
        {
            GameId = gameId;
            _definitionsByClass = definitionsByClass;
            _onAutomaticClipBookmark = onAutomaticClipBookmark;
            var definitionsById = definitions
                .GroupBy(definition => definition.Id)
                .ToDictionary(group => group.Key, group => group.First());
            _ocrTracker = new OcrTextTracker(definitionsById.Values);
            _triggerCounter = new TriggerCounter(gameId, definitionsById);
        }

        internal void OnDetections(DetectionBatch batch)
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
                    ProcessDetections(batch ?? new DetectionBatch { FrameTimestamp = DateTime.UtcNow });
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

        private void ProcessDetections(DetectionBatch batch)
        {
            var now = batch.FrameTimestamp;
            var detections = batch.ObjectDetections;
            if (detections.Count > 0 && Log.IsEnabled(LogEventLevel.Debug))
            {
                Log.Debug("DetectionHost: received {Count} detection(s) for {GameId}: classes {Classes}",
                    detections.Count, GameId, string.Join(",", detections.Select(d => d.ClassId).Distinct()));
            }

            var resolvedObjects = new List<(DetectionResult Detection, EventDefinition Definition)>();
            foreach (var detection in detections)
            {
                if (!_definitionsByClass.TryGetValue(detection.ClassId, out var definition))
                {
                    Log.Warning("DetectionHost: no event definition for class {ClassId} of {GameId}; dropping",
                        detection.ClassId, GameId);
                    continue;
                }

                resolvedObjects.Add((detection, definition));
            }

            var activeOcr = _ocrTracker.Update(batch.OcrMatches, now);
            _triggerCounter.Count(resolvedObjects, activeOcr,
                (definition, increase) => CreateBookmarks(definition, increase, now));
        }

        private const int MaxNewOccurrencesPerCycle = 8;

        private void CreateBookmarks(EventDefinition definition, int count, DateTime now)
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

            var effectiveCount = Math.Min(count, MaxNewOccurrencesPerCycle);
            if (effectiveCount < count)
                Log.Warning("CreateBookmark: clamped {Requested} new '{EventName}' occurrence(s) to {Cap} for one cycle",
                    count, definition.Name, MaxNewOccurrencesPerCycle);

            for (var index = 0; index < effectiveCount; index++)
            {
                var bookmark = new Bookmark
                {
                    Type = bookmarkType,
                    Time = now - recording.StartTimeUtc,
                };
                recording.AddBookmark(bookmark);
                if (definition.IncludeInAutoClips)
                    _onAutomaticClipBookmark?.Invoke(bookmark);
            }

            Log.Information("CreateBookmark: created {Count} {Type} bookmark(s) for '{EventName}'",
                effectiveCount, bookmarkType, definition.Name);
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

public interface ITrackDefinitionSource
{
    bool HasModelForGame(string gameId);

    bool HasDetectionBundleForGame(string gameId);

    List<EventDefinition> LoadEventDefinitions(string gameId);
}

internal sealed class ModelServiceDefinitionSource : ITrackDefinitionSource
{
    public bool HasModelForGame(string gameId) => ModelService.HasModelForGame(gameId);

    public bool HasDetectionBundleForGame(string gameId) => ModelService.HasDetectionBundleForGame(gameId);

    public List<EventDefinition> LoadEventDefinitions(string gameId)
        => ModelService.LoadEventDefinitions(gameId);
}
