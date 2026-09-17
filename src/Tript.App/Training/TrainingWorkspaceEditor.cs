// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

#if TRIPT_TRAINING

using System.Text.Json;
using Serilog;
using Tript.Detection;

namespace Tript.App.Training;

internal sealed record InvalidTrainingSample(string Id, string? Reason);

internal static class TrainingWorkspaceEditor
{
    internal static TrainingWorkspace EnsureWorkspace(string gameId)
    {
        var workspace = TrainingWorkspace.ForGame(gameId);
        if (File.Exists(workspace.EventsPath))
            return workspace;

        var runtimeRoot = ModelService.GetGamePath(gameId);
        var runtimeEvents = Path.Combine(runtimeRoot, "events.json");
        if (File.Exists(runtimeEvents))
        {
            workspace.EnsureDirectories();
            File.Copy(runtimeEvents, workspace.EventsPath, overwrite: true);
            var runtimeGroups = Path.Combine(runtimeRoot, "regionGroups.json");
            if (File.Exists(runtimeGroups))
                File.Copy(runtimeGroups, workspace.RegionGroupsPath, overwrite: true);
            var runtimeModel = Path.Combine(runtimeRoot, "model.onnx");
            if (File.Exists(runtimeModel))
                File.Copy(runtimeModel, workspace.ModelPath, overwrite: true);
            var runtimeOcrModel = Path.Combine(runtimeRoot, "ocr_model.onnx");
            var runtimeOcrDetector = Path.Combine(runtimeRoot, "ocr_detector.onnx");
            var runtimeOcrDict = Path.Combine(runtimeRoot, "ocr_dict.txt");
            if (File.Exists(runtimeOcrModel) && File.Exists(runtimeOcrDict))
            {
                File.Copy(runtimeOcrModel, Path.Combine(workspace.RootPath, "ocr_model.onnx"), overwrite: true);
                File.Copy(runtimeOcrDict, Path.Combine(workspace.RootPath, "ocr_dict.txt"), overwrite: true);
                if (File.Exists(runtimeOcrDetector))
                    File.Copy(runtimeOcrDetector, workspace.OcrDetectorPath, overwrite: true);
            }
        }
        return workspace;
    }

    internal static int ReplaceEvents(TrainingWorkspace workspace, List<EventDefinition> orderedEvents,
        Action<int> reportDeletePercent)
    {
        TrainingEventValidator.ValidateDetectionKinds(orderedEvents);
        var objectEvents = orderedEvents
            .Where(eventDefinition => eventDefinition.DetectionKind == DetectionKind.Object)
            .OrderBy(eventDefinition => eventDefinition.ClassId)
            .ToList();
        var regionGroups = workspace.LoadRegionGroups();
        var classIdMap = objectEvents
            .Select((eventDefinition, index) => new { Old = eventDefinition.ClassId, New = index })
            .ToDictionary(pair => pair.Old, pair => pair.New);
        var sampleStore = new TrainingSampleStore(workspace);
        var existingClassIds = workspace.LoadDefinitions()
            .Where(eventDefinition => eventDefinition.DetectionKind == DetectionKind.Object)
            .Select(eventDefinition => eventDefinition.ClassId).ToHashSet();
        var deletesClass = existingClassIds.Any(classId => !classIdMap.ContainsKey(classId));
        foreach (var eventDefinition in orderedEvents.Where(eventDefinition =>
                     eventDefinition.DetectionKind == DetectionKind.Ocr || !eventDefinition.FixedPosition))
        {
            if (eventDefinition.DetectionKind == DetectionKind.Ocr)
                eventDefinition.FixedPosition = false;
            eventDefinition.FixedLabelCenterX = null;
            eventDefinition.FixedLabelCenterY = null;
            eventDefinition.FixedLabelWidth = null;
            eventDefinition.FixedLabelHeight = null;
        }
        InitializeFixedPositions(orderedEvents, sampleStore.List());
        TrainingEventValidator.ValidateRegions(orderedEvents);
        TrainingEventValidator.ValidateRegionGroupReferences(orderedEvents, regionGroups);
        TrainingEventValidator.ValidateFixedPositions(orderedEvents);
        TrainingEventValidator.ValidateSubtractorReferences(orderedEvents);
        var lastReportedPercent = -1;
        Action<int, int>? deleteProgress = deletesClass ? (completed, total) =>
        {
            var percent = (int)Math.Floor(completed * 100.0 / Math.Max(1, total));
            if (percent == lastReportedPercent) return;
            lastReportedPercent = percent;
            reportDeletePercent(percent);
        } : null;
        var fixedPositions = orderedEvents
            .Where(eventDefinition => eventDefinition.DetectionKind == DetectionKind.Object
                && eventDefinition.FixedPosition
                && eventDefinition.FixedLabelCenterX is not null
                && eventDefinition.FixedLabelCenterY is not null
                && eventDefinition.FixedLabelWidth is not null
                && eventDefinition.FixedLabelHeight is not null)
            .ToDictionary(eventDefinition => eventDefinition.ClassId, eventDefinition => new TrainingLabel
            {
                ClassId = eventDefinition.ClassId,
                CenterX = eventDefinition.FixedLabelCenterX!.Value,
                CenterY = eventDefinition.FixedLabelCenterY!.Value,
                Width = eventDefinition.FixedLabelWidth!.Value,
                Height = eventDefinition.FixedLabelHeight!.Value,
            });
        var remap = sampleStore.RemapClassIds(classIdMap, fixedPositions, deleteProgress);
        foreach (var eventDefinition in objectEvents)
            eventDefinition.ClassId = classIdMap[eventDefinition.ClassId];
        foreach (var eventDefinition in orderedEvents.Where(eventDefinition =>
                     eventDefinition.DetectionKind == DetectionKind.Ocr))
            eventDefinition.ClassId = -1;
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        try
        {
            var serializedEvents = JsonSerializer.SerializeToUtf8Bytes(orderedEvents, options);
            TrainingSampleStore.WriteAtomically(workspace.EventsPath, serializedEvents);
        }
        catch
        {
            sampleStore.RestoreMetadata(remap.Originals);
            throw;
        }
        return remap.RemovedLabelCount;
    }

    internal static void ReplaceRegionGroups(TrainingWorkspace workspace,
        List<TrainingRegionGroup> regionGroups)
    {
        var events = workspace.LoadDefinitions();
        var groupIds = regionGroups.Select(group => group.Id).ToHashSet();
        var detached = false;
        foreach (var eventDefinition in events)
        {
            if (eventDefinition.RegionGroupId is int groupId && !groupIds.Contains(groupId))
            {
                eventDefinition.RegionGroupId = null;
                detached = true;
            }
        }

        var originals = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase)
        {
            [workspace.EventsPath] = File.Exists(workspace.EventsPath)
                ? File.ReadAllBytes(workspace.EventsPath) : null,
            [workspace.RegionGroupsPath] = File.Exists(workspace.RegionGroupsPath)
                ? File.ReadAllBytes(workspace.RegionGroupsPath) : null,
        };
        try
        {
            workspace.SaveRegionGroups(regionGroups);
            if (detached)
            {
                TrainingSampleStore.WriteAtomically(workspace.EventsPath,
                    JsonSerializer.SerializeToUtf8Bytes(events,
                        TrainingRegionResolver.WriteJsonOptions));
            }
        }
        catch
        {
            new TrainingSampleStore(workspace).RestoreMetadata(originals);
            throw;
        }
    }

    internal static List<InvalidTrainingSample> FindInvalidSamples(IReadOnlyList<TrainingSampleRecord> samples,
        IReadOnlyList<EventDefinition> definitions, IReadOnlyList<TrainingRegionGroup> regionGroups) =>
        samples.Select(sample =>
        {
            var labelError = TrainingLabelValidator.FindError(sample.Labels, definitions,
                requireLabel: false, regionGroups: regionGroups);
            var regionError = TrainingOcrRegionValidator.FindError(sample.OcrRegions);
            var reason = labelError ?? regionError;
            if (reason is null && sample.Labels.Count == 0 && sample.OcrRegions.Count == 0)
                reason = "a sample must contain an object label or OCR region";
            return new InvalidTrainingSample(sample.Id, reason);
        }).Where(sample => sample.Reason is not null).ToList();

    internal static int CountImages(string datasetPath, string split)
    {
        var path = Path.Combine(datasetPath, "images", split);
        if (!Directory.Exists(path))
            return 0;
        try
        {
            return Directory.EnumerateFiles(path)
                .Count(file => !Path.GetFileName(file).Contains(".tmp-", StringComparison.OrdinalIgnoreCase)
                    && IsImage(file));
        }
        catch (Exception exception) when (TrainingWorkspace.IsTransientFileSystemError(exception))
        {
            return 0;
        }
    }

    private static bool IsImage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg";

    internal static string ResolveModelPath(string installedModelPath, string workspaceModelPath) =>
        File.Exists(installedModelPath) ? installedModelPath : workspaceModelPath;

    internal static void RefreshWorkspaceModel(TrainingWorkspace workspace, string sourcePath)
    {
        try
        {
            File.Copy(sourcePath, workspace.ModelPath, overwrite: true);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Training: could not refresh the workspace model copy for {GameId}",
                workspace.GameId);
        }
    }

    internal static string DescribeOutcome(bool trainedObject, bool trainedOcr, bool ocrFineTuned)
    {
        var parts = new List<string>();
        if (trainedObject) parts.Add("object detector");
        if (trainedOcr)
            parts.Add(ocrFineTuned
                ? "fine-tuned OCR recogniser"
                : "OCR recogniser (kept the pretrained model)");
        return parts.Count == 0
            ? "Training complete."
            : $"Training complete: installed and activated the {string.Join(" and ", parts)}.";
    }

    internal static TrainingLabel ToLabel(TrainingLabelParameters label) => new()
    {
        ClassId = label.ClassId,
        CenterX = label.CenterX,
        CenterY = label.CenterY,
        Width = label.Width,
        Height = label.Height,
    };

    internal static TrainingOcrRegion ToOcrRegion(TrainingOcrRegionParameters region) => new()
    {
        X = region.X,
        Y = region.Y,
        Width = region.Width,
        Height = region.Height,
        Text = region.Text.Trim(),
    };

    private static void InitializeFixedPositions(IReadOnlyList<EventDefinition> events,
        IReadOnlyList<TrainingSampleRecord> samples)
    {
        foreach (var eventDefinition in events.Where(eventDefinition => eventDefinition.FixedPosition
                     && eventDefinition.FixedLabelCenterX is null
                     && eventDefinition.FixedLabelCenterY is null
                     && eventDefinition.FixedLabelWidth is null
                     && eventDefinition.FixedLabelHeight is null))
        {
            var label = samples.SelectMany(sample => sample.Labels)
                .Where(candidate => candidate.ClassId == eventDefinition.ClassId)
                .OrderBy(candidate => candidate.Width * candidate.Height)
                .FirstOrDefault();
            if (label is null) continue;
            eventDefinition.FixedLabelCenterX = label.CenterX;
            eventDefinition.FixedLabelCenterY = label.CenterY;
            eventDefinition.FixedLabelWidth = label.Width;
            eventDefinition.FixedLabelHeight = label.Height;
        }
    }
}

#endif
