// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Detection;
using Tript.Obs;
using Tript.Core;
using Serilog;

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
        private readonly IReadOnlyDictionary<int, EventDefinition> _definitionsById;
        private readonly Action<Bookmark>? _onAutomaticClipBookmark;
        private readonly object _processingGate = new();
        private readonly Dictionary<int, int> _previousNetCounts = new();
        private readonly Dictionary<int, List<TextTrack>> _ocrTracks = new();

        private const float VetoConfidenceFloor = 0.8f;
        private const int VetoMemoryMilliseconds = 1000;
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
            _definitionsById = definitions
                .GroupBy(definition => definition.Id)
                .ToDictionary(group => group.Key, group => group.First());
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
                    ProcessDetections(batch ?? new DetectionBatch { FrameTimestamp = DateTime.Now });
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
            Log.Information("DetectionHost: received {Count} detection(s) for {GameId}: classes {Classes}",
                detections.Count, GameId, string.Join(",", detections.Select(d => d.ClassId).Distinct()));

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

            var activeOcrTracks = UpdateOcrTracks(batch.OcrMatches, now);

            var hasExclusion = resolvedObjects.Any(item => item.Definition.Type == EventType.Exclusion)
                || activeOcrTracks.Any(item => item.Definition.Type == EventType.Exclusion);
            if (hasExclusion)
            {
                foreach (var definition in _definitionsById.Values.Where(item => item.Type == EventType.Trigger))
                {
                    var observedCount = definition.DetectionKind == DetectionKind.Ocr
                        ? activeOcrTracks.Count(item => item.Definition.Id == definition.Id)
                        : DetectionBatchCounter.DistinctDetections(
                            resolvedObjects.Where(item => item.Definition.Type == EventType.Trigger
                                    && item.Definition.Id == definition.Id)
                                .Select(item => item.Detection).ToList()).Count;
                    _previousNetCounts[definition.Id] = Math.Max(
                        _previousNetCounts.GetValueOrDefault(definition.Id), observedCount);
                }

                Log.Information("DetectionHost: exclusion detected for {GameId}; suppressing {Count} event(s) in this cycle",
                    GameId, resolvedObjects.Count + activeOcrTracks.Count);
                return;
            }

            var subtractionCounts = new Dictionary<int, int>();
            foreach (var subtractorGroup in resolvedObjects
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
            foreach (var subtractorGroup in activeOcrTracks
                .Where(item => item.Definition.Type == EventType.Subtractor)
                .GroupBy(item => item.Definition.Id))
            {
                var subtractor = subtractorGroup.First().Definition;
                if (subtractor.SubtractsEventId is not int targetId
                    || !_definitionsById.TryGetValue(targetId, out var target)
                    || target.Type != EventType.Trigger)
                {
                    Log.Warning("DetectionHost: OCR subtractor in {GameId} has an invalid target; ignoring it", GameId);
                    continue;
                }

                subtractionCounts[target.Id] = subtractionCounts.GetValueOrDefault(target.Id)
                    + subtractorGroup.Count();
            }

            var currentNetCounts = new Dictionary<int, int>();
            foreach (var definition in _definitionsById.Values.Where(item => item.Type == EventType.Trigger))
            {
                var grossCount = definition.DetectionKind == DetectionKind.Ocr
                    ? activeOcrTracks.Count(item => item.Definition.Id == definition.Id)
                    : DetectionBatchCounter.DistinctDetections(
                        resolvedObjects.Where(item => item.Definition.Type == EventType.Trigger
                                && item.Definition.Id == definition.Id)
                            .Select(item => item.Detection).ToList()).Count;
                subtractionCounts.TryGetValue(definition.Id, out var subtractions);
                var netCount = Math.Max(0, grossCount - subtractions);
                currentNetCounts[definition.Id] = netCount;

                _previousNetCounts.TryGetValue(definition.Id, out var previousNetCount);
                var increase = netCount - previousNetCount;
                if (increase > 0)
                    CreateBookmarks(definition, increase, now);
            }

            _previousNetCounts.Clear();
            foreach (var (eventId, count) in currentNetCounts)
                _previousNetCounts[eventId] = count;
        }

        private List<(TextTrack Track, EventDefinition Definition)> UpdateOcrTracks(
            IReadOnlyList<OcrMatch> matches, DateTime now)
        {
            foreach (var definition in _definitionsById.Values.Where(definition =>
                         definition.DetectionKind == DetectionKind.Ocr))
            {
                if (!_ocrTracks.TryGetValue(definition.Id, out var tracks))
                {
                    tracks = [];
                    _ocrTracks[definition.Id] = tracks;
                }

                foreach (var track in tracks) track.SeenThisBatch = false;
                foreach (var match in matches.Where(match => match.EventId == definition.Id)
                             .GroupBy(match => new
                             {
                                 match.SegmentId,
                                 Text = string.IsNullOrWhiteSpace(match.NormalizedText)
                                     ? OcrTextNormalizer.Normalize(match.Text)
                                     : match.NormalizedText,
                                 match.X,
                                 match.Y,
                                 match.Width,
                                 match.Height,
                             })
                             .Select(group => group.First()))
                {
                    var normalized = string.IsNullOrWhiteSpace(match.NormalizedText)
                        ? OcrTextNormalizer.Normalize(match.Text)
                        : match.NormalizedText;
                    if (normalized.Length == 0) continue;
                    if (definition.Type != EventType.Trigger && match.Confidence < VetoConfidenceFloor)
                        continue;

                    var track = tracks
                        .Where(candidate => !candidate.SeenThisBatch && TextDistance(candidate.Text, normalized)
                            <= (definition.Ocr?.Tracking.MaximumTextDistance ?? 0.2))
                        .OrderByDescending(candidate => BoundsAffinity(candidate, match,
                            definition.Ocr?.Tracking.MinimumBoundsIou ?? 0.3f))
                        .FirstOrDefault(candidate => BoundsAffinity(candidate, match,
                            definition.Ocr?.Tracking.MinimumBoundsIou ?? 0.3f) >= 0);
                    if (track is null)
                    {
                        track = new TextTrack
                        {
                            Text = normalized,
                            FirstSeen = now,
                            LastSeen = now,
                            X = match.X,
                            Y = match.Y,
                            Width = match.Width,
                            Height = match.Height,
                        };
                        tracks.Add(track);
                    }
                    else
                    {
                        if (track.ConsecutiveMatches == 0) track.FirstSeen = now;
                        track.Text = normalized;
                        track.LastSeen = now;
                        track.X = match.X;
                        track.Y = match.Y;
                        track.Width = match.Width;
                        track.Height = match.Height;
                    }

                    track.SeenThisBatch = true;
                    track.ConsecutiveMatches++;
                    var tracking = definition.Ocr?.Tracking ?? new OcrTrackingDefinition();
                    if (!track.Confirmed && (track.ConsecutiveMatches >= tracking.ConfirmationFrames
                        || now - track.FirstSeen >= TimeSpan.FromMilliseconds(tracking.MinimumStableMilliseconds)))
                    {
                        track.Confirmed = true;
                    }
                }

                foreach (var track in tracks.Where(track => !track.SeenThisBatch))
                    track.ConsecutiveMatches = 0;
                var expiryMs = definition.Ocr?.Tracking.ExpireAfterMissingMilliseconds ?? 350;
                if (definition.Type != EventType.Trigger)
                    expiryMs = Math.Max(expiryMs, VetoMemoryMilliseconds);
                var expiry = TimeSpan.FromMilliseconds(expiryMs);
                tracks.RemoveAll(track => now - track.LastSeen > expiry);
            }

            return _ocrTracks.SelectMany(pair => pair.Value
                    .Where(track => track.Confirmed || _definitionsById[pair.Key].Type != EventType.Trigger)
                    .Select(track => (track, _definitionsById[pair.Key])))
                .ToList();
        }

        private static double BoundsAffinity(TextTrack track, OcrMatch match, float minimumIou)
        {
            var left = Math.Max(track.X, match.X);
            var top = Math.Max(track.Y, match.Y);
            var right = Math.Min(track.X + track.Width, match.X + match.Width);
            var bottom = Math.Min(track.Y + track.Height, match.Y + match.Height);
            var intersection = Math.Max(0, right - left) * Math.Max(0, bottom - top);
            var union = track.Width * track.Height + match.Width * match.Height - intersection;
            if (union > 0 && intersection / union >= minimumIou) return intersection / union;

            var centerDistance = Math.Abs(track.X + track.Width / 2 - match.X - match.Width / 2)
                + Math.Abs(track.Y + track.Height / 2 - match.Y - match.Height / 2);
            return centerDistance <= Math.Max(track.Height, match.Height) * 1.5 ? 0 : -1;
        }

        private static double TextDistance(string left, string right)
        {
            if (left == right) return 0;
            var previous = Enumerable.Range(0, right.Length + 1).ToArray();
            var current = new int[right.Length + 1];
            for (var row = 1; row <= left.Length; row++)
            {
                current[0] = row;
                for (var column = 1; column <= right.Length; column++)
                {
                    current[column] = Math.Min(Math.Min(previous[column] + 1, current[column - 1] + 1),
                        previous[column - 1] + (left[row - 1] == right[column - 1] ? 0 : 1));
                }
                (previous, current) = (current, previous);
            }
            return (double)previous[right.Length] / Math.Max(left.Length, right.Length);
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
                    Time = now - recording.StartTime,
                };
                recording.AddBookmark(bookmark);
                if (definition.IncludeInAutoClips)
                    _onAutomaticClipBookmark?.Invoke(bookmark);
            }

            Log.Information("CreateBookmark: created {Count} {Type} bookmark(s) for '{EventName}'",
                effectiveCount, bookmarkType, definition.Name);
        }

        private sealed class TextTrack
        {
            internal string Text { get; set; } = string.Empty;
            internal DateTime FirstSeen { get; set; }
            internal DateTime LastSeen { get; set; }
            internal int ConsecutiveMatches { get; set; }
            internal bool Confirmed { get; set; }
            internal bool SeenThisBatch { get; set; }
            internal float X { get; set; }
            internal float Y { get; set; }
            internal float Width { get; set; }
            internal float Height { get; set; }
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
