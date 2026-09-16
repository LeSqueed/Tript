// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Serilog;
using Tript.Detection;

namespace Tript.Recorder;

internal sealed class TriggerCounter
{
    private readonly string _gameId;
    private readonly IReadOnlyDictionary<int, EventDefinition> _definitionsById;
    private readonly IReadOnlyList<EventDefinition> _triggers;
    private readonly Dictionary<int, int> _previousNetCounts = new();

    internal TriggerCounter(string gameId, IReadOnlyDictionary<int, EventDefinition> definitionsById)
    {
        _gameId = gameId;
        _definitionsById = definitionsById;
        _triggers = definitionsById.Values.Where(definition => definition.Type == EventType.Trigger).ToList();
    }

    internal void Count(IReadOnlyList<(DetectionResult Detection, EventDefinition Definition)> objects,
        IReadOnlyList<EventDefinition> activeOcr, Action<EventDefinition, int> onIncrease)
    {
        var triggerObjects = objects
            .Where(item => item.Definition.Type == EventType.Trigger)
            .GroupBy(item => item.Definition.Id)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Detection).ToList());
        var ocrCounts = activeOcr
            .GroupBy(definition => definition.Id)
            .ToDictionary(group => group.Key, group => group.Count());

        int GrossCount(EventDefinition definition) => definition.DetectionKind == DetectionKind.Ocr
            ? ocrCounts.GetValueOrDefault(definition.Id)
            : triggerObjects.TryGetValue(definition.Id, out var detections)
                ? DetectionBatchCounter.DistinctDetections(detections).Count
                : 0;

        if (objects.Any(item => item.Definition.Type == EventType.Exclusion)
            || activeOcr.Any(definition => definition.Type == EventType.Exclusion))
        {
            foreach (var definition in _triggers)
            {
                _previousNetCounts[definition.Id] = Math.Max(
                    _previousNetCounts.GetValueOrDefault(definition.Id), GrossCount(definition));
            }

            Log.Information("DetectionHost: exclusion detected for {GameId}; suppressing {Count} event(s) in this cycle",
                _gameId, objects.Count + activeOcr.Count);
            return;
        }

        var subtractions = Subtractions(objects, activeOcr);
        var currentNetCounts = new Dictionary<int, int>();
        foreach (var definition in _triggers)
        {
            var netCount = Math.Max(0, GrossCount(definition) - subtractions.GetValueOrDefault(definition.Id));
            currentNetCounts[definition.Id] = netCount;

            var increase = netCount - _previousNetCounts.GetValueOrDefault(definition.Id);
            if (increase > 0)
                onIncrease(definition, increase);
        }

        _previousNetCounts.Clear();
        foreach (var (eventId, count) in currentNetCounts)
            _previousNetCounts[eventId] = count;
    }

    private Dictionary<int, int> Subtractions(
        IReadOnlyList<(DetectionResult Detection, EventDefinition Definition)> objects,
        IReadOnlyList<EventDefinition> activeOcr)
    {
        var subtractions = new Dictionary<int, int>();
        foreach (var group in objects
            .Where(item => item.Definition.Type == EventType.Subtractor)
            .GroupBy(item => item.Definition.Id))
        {
            if (TargetOf(group.First().Definition) is not { } target)
            {
                Log.Warning("DetectionHost: subtractor in {GameId} has an invalid target; ignoring it", _gameId);
                continue;
            }

            subtractions[target.Id] = subtractions.GetValueOrDefault(target.Id)
                + DetectionBatchCounter.DistinctDetections(group.Select(item => item.Detection).ToList()).Count;
        }

        foreach (var group in activeOcr
            .Where(definition => definition.Type == EventType.Subtractor)
            .GroupBy(definition => definition.Id))
        {
            if (TargetOf(group.First()) is not { } target)
            {
                Log.Warning("DetectionHost: OCR subtractor in {GameId} has an invalid target; ignoring it", _gameId);
                continue;
            }

            subtractions[target.Id] = subtractions.GetValueOrDefault(target.Id) + group.Count();
        }

        return subtractions;
    }

    private EventDefinition? TargetOf(EventDefinition subtractor) =>
        subtractor.SubtractsEventId is int targetId
        && _definitionsById.TryGetValue(targetId, out var target)
        && target.Type == EventType.Trigger
            ? target
            : null;
}
