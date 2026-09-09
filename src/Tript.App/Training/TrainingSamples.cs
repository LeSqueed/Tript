// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

#if TRIPT_TRAINING

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using Tript.Detection;

namespace Tript.App.Training;

internal sealed class TrainingLabel
{
    public int ClassId { get; set; }

    public double CenterX { get; set; }

    public double CenterY { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }
}

internal sealed class TrainingOcrRegion
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string Text { get; set; } = string.Empty;
}

internal sealed class TrainingLabelSuggestion
{
    public TrainingLabel Label { get; init; } = new();

    public float Confidence { get; init; }
}

internal static class TrainingLabelSuggestionFilter
{
    private const float OverlapIouThreshold = 0.3f;

    internal static List<TrainingLabelSuggestion> Merge(
        IReadOnlyList<TrainingLabel> existing, IReadOnlyList<DetectionResult> detections,
        IReadOnlyList<EventDefinition> definitions,
        IReadOnlyList<TrainingRegionGroup>? regionGroups = null)
    {
        var regions = TrainingRegionResolver.ResolveByClassId(definitions, regionGroups ?? []);
        var definitionsByClass = definitions
            .Where(definition => definition.DetectionKind == DetectionKind.Object)
            .ToDictionary(definition => definition.ClassId);
        var accepted = existing.Select(ToBox).ToList();
        var suggestions = new List<TrainingLabelSuggestion>();
        foreach (var detection in detections.OrderByDescending(detection => detection.Confidence))
        {
            if (!float.IsFinite(detection.X) || !float.IsFinite(detection.Y)
                || !float.IsFinite(detection.Width) || !float.IsFinite(detection.Height)
                || detection.Width <= 0 || detection.Height <= 0
                || detection.X < 0 || detection.Y < 0
                || detection.X + detection.Width > 1 || detection.Y + detection.Height > 1)
                continue;

            if (!definitionsByClass.TryGetValue(detection.ClassId, out var definition))
                continue;

            // A fixed-position event has one authoritative label geometry. The model only
            // indicates presence; the inserted box must be the canonical one, never the
            // detector's. An uninitialized fixed event has no geometry to trust yet, so skip it
            // until a fixed position is set manually (otherwise a suggestion would seed it).
            TrainingLabel label;
            if (definition.FixedPosition && definition.FixedLabelCenterX is not null
                && definition.FixedLabelCenterY is not null && definition.FixedLabelWidth is not null
                && definition.FixedLabelHeight is not null)
            {
                label = new TrainingLabel
                {
                    ClassId = detection.ClassId,
                    CenterX = definition.FixedLabelCenterX.Value,
                    CenterY = definition.FixedLabelCenterY.Value,
                    Width = definition.FixedLabelWidth.Value,
                    Height = definition.FixedLabelHeight.Value,
                };
            }
            else if (definition.FixedPosition)
            {
                continue;
            }
            else
            {
                label = new TrainingLabel
                {
                    ClassId = detection.ClassId,
                    CenterX = detection.X + detection.Width / 2,
                    CenterY = detection.Y + detection.Height / 2,
                    Width = detection.Width,
                    Height = detection.Height,
                };
            }

            // An event's screen region is the exact area that becomes a training crop. A detection
            // that bleeds outside it would accept a label the exporter cannot fit into the crop,
            // producing out-of-bounds coordinates Ultralytics silently drops. Only offer boxes fully
            // inside the event's region; an event without one crops the whole frame, so any box fits.
            regions.TryGetValue(detection.ClassId, out var region);
            if (region is not null && !TrainingRegionResolver.Contains(region.Value, label))
                continue;

            var box = ToBox(label);
            if (accepted.Any(existingBox => IoU(existingBox, box) >= OverlapIouThreshold))
                continue;

            accepted.Add(box);
            suggestions.Add(new TrainingLabelSuggestion
            {
                Label = label,
                Confidence = detection.Confidence,
            });
        }

        return suggestions;
    }

    private static Box ToBox(TrainingLabel label) => new(
        (float)(label.CenterX - label.Width / 2),
        (float)(label.CenterY - label.Height / 2),
        (float)label.Width,
        (float)label.Height);

    private static Box ToBox(DetectionResult detection) => new(
        detection.X, detection.Y, detection.Width, detection.Height);

    private static float IoU(Box left, Box right)
    {
        var leftArea = left.Width * left.Height;
        var rightArea = right.Width * right.Height;
        if (leftArea <= 0 || rightArea <= 0) return 1f;

        var overlapWidth = MathF.Min(left.X + left.Width, right.X + right.Width)
            - MathF.Max(left.X, right.X);
        var overlapHeight = MathF.Min(left.Y + left.Height, right.Y + right.Height)
            - MathF.Max(left.Y, right.Y);
        if (overlapWidth <= 0 || overlapHeight <= 0) return 0;

        var intersection = overlapWidth * overlapHeight;
        return intersection / (leftArea + rightArea - intersection);
    }

    private readonly record struct Box(float X, float Y, float Width, float Height);
}

internal sealed class TrainingSampleRecord
{
    public string Id { get; set; } = string.Empty;

    public string ImageFile { get; set; } = string.Empty;

    public string SourcePath { get; set; } = string.Empty;

    public double TimestampSeconds { get; set; }

    public int ImageWidth { get; set; }

    public int ImageHeight { get; set; }

    public List<TrainingLabel> Labels { get; set; } = [];

    public List<TrainingOcrRegion> OcrRegions { get; set; } = [];

    // Imported dataset images are already cropped to the model input. Keep them in the imported
    // dataset instead of sending them through the full-frame crop exporter a second time.
    public string? DatasetImagePath { get; set; }
}

internal static class TrainingLabelValidator
{
    internal static string? FindError(IReadOnlyList<TrainingLabel> labels,
        IReadOnlyList<EventDefinition> definitions, bool requireLabel = true,
        IReadOnlyList<TrainingRegionGroup>? regionGroups = null)
    {
        if (requireLabel && labels.Count == 0)
            return "a sample must contain at least one label";

        var definitionsByClass = definitions
            .Where(definition => definition.DetectionKind == DetectionKind.Object)
            .ToDictionary(definition => definition.ClassId);
        var groups = regionGroups ?? [];
        var regions = TrainingRegionResolver.ResolveByClassId(definitions, groups);
        for (var index = 0; index < labels.Count; index++)
        {
            var label = labels[index];
            if (!definitionsByClass.TryGetValue(label.ClassId, out var definition))
                return $"label {index} uses unknown classId {label.ClassId}";
            if (definition.RegionGroupId is int groupId
                && !groups.Any(group => group.Id == groupId))
            {
                return $"label {index} references a missing region group through '{definition.Name}'";
            }

            if (!double.IsFinite(label.CenterX) || !double.IsFinite(label.CenterY)
                || !double.IsFinite(label.Width) || !double.IsFinite(label.Height))
            {
                return $"label {index} contains a non-finite coordinate";
            }

            if (label.Width <= 0 || label.Height <= 0)
                return $"label {index} has no area";

            var left = label.CenterX - label.Width / 2;
            var top = label.CenterY - label.Height / 2;
            var right = label.CenterX + label.Width / 2;
            var bottom = label.CenterY + label.Height / 2;
            if (left < 0 || top < 0 || right > 1 || bottom > 1)
                return $"label {index} lies outside the image bounds";

            if (regions[label.ClassId] is TrainingScreenRegion region
                && !TrainingRegionResolver.Contains(region, label))
            {
                return $"label {index} lies outside the '{definition.Name}' screen region";
            }
        }

        return null;
    }

    // Saves must never be blocked by crop containment: the user can deliberately place a label
    // outside its effective region ("erroneous"), which training/export then skips as invalid.
    // Only structural corruption (unknown class, unusable coordinates, out-of-frame) is fatal.
    internal static string? FindBlockingError(IReadOnlyList<TrainingLabel> labels,
        IReadOnlyList<EventDefinition> definitions)
    {
        var definitionsByClass = definitions
            .Where(definition => definition.DetectionKind == DetectionKind.Object)
            .ToDictionary(definition => definition.ClassId);
        for (var index = 0; index < labels.Count; index++)
        {
            var label = labels[index];
            if (!definitionsByClass.ContainsKey(label.ClassId))
                return $"label {index} uses unknown classId {label.ClassId}";
            if (!double.IsFinite(label.CenterX) || !double.IsFinite(label.CenterY)
                || !double.IsFinite(label.Width) || !double.IsFinite(label.Height))
            {
                return $"label {index} contains a non-finite coordinate";
            }
            if (label.Width <= 0 || label.Height <= 0)
                return $"label {index} has no area";
            var left = label.CenterX - label.Width / 2;
            var top = label.CenterY - label.Height / 2;
            var right = label.CenterX + label.Width / 2;
            var bottom = label.CenterY + label.Height / 2;
            if (left < 0 || top < 0 || right > 1 || bottom > 1)
                return $"label {index} lies outside the image bounds";
        }
        return null;
    }
}

internal static class TrainingOcrRegionValidator
{
    private const double Tolerance = 1e-4;

    internal static string? FindError(IReadOnlyList<TrainingOcrRegion> regions)
    {
        for (var index = 0; index < regions.Count; index++)
        {
            var region = regions[index];
            if (!double.IsFinite(region.X) || !double.IsFinite(region.Y)
                || !double.IsFinite(region.Width) || !double.IsFinite(region.Height))
            {
                return $"OCR region {index} contains a non-finite coordinate";
            }
            if (region.Width <= 0 || region.Height <= 0)
                return $"OCR region {index} has no area";
            if (region.X < -Tolerance || region.Y < -Tolerance
                || region.X + region.Width > 1 + Tolerance
                || region.Y + region.Height > 1 + Tolerance)
            {
                return $"OCR region {index} lies outside the image bounds";
            }
            if (string.IsNullOrWhiteSpace(region.Text))
                return $"OCR region {index} is empty";
        }
        return null;
    }
}

internal sealed class TrainingSampleStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly TrainingWorkspace _workspace;

    internal TrainingSampleStore(TrainingWorkspace workspace)
    {
        _workspace = workspace;
    }

    internal TrainingSampleRecord Save(string sourcePath, double timestampSeconds, int imageWidth,
        int imageHeight, IReadOnlyList<TrainingLabel> labels, ReadOnlySpan<byte> png,
        IReadOnlyList<EventDefinition> definitions, string? datasetImagePath = null,
        IReadOnlyList<TrainingRegionGroup>? regionGroups = null)
    {
        _workspace.EnsureDirectories();
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("A sample requires its source video path.", nameof(sourcePath));
        if (!double.IsFinite(timestampSeconds) || timestampSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(timestampSeconds));
        if (imageWidth <= 0 || imageHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(imageWidth), "Image dimensions must be positive.");

        // Captured frames start unlabeled and are completed in the sample editor. Dataset export
        // still requires labels before a sample can be used for training.
        var labelError = TrainingLabelValidator.FindError(labels, definitions, requireLabel: false,
            regionGroups);
        if (labelError is not null)
            throw new InvalidDataException($"Invalid training sample: {labelError}.");
        if (png.Length == 0)
            throw new InvalidDataException("A training sample image cannot be empty.");

        var id = SampleId(sourcePath, timestampSeconds);
        var imageFile = id + ".png";
        var record = new TrainingSampleRecord
        {
            Id = id,
            ImageFile = imageFile,
            SourcePath = Path.GetFullPath(sourcePath),
            TimestampSeconds = timestampSeconds,
            ImageWidth = imageWidth,
            ImageHeight = imageHeight,
            Labels = labels.Select(Clone).ToList(),
            DatasetImagePath = datasetImagePath,
        };

        var imagePath = Path.Combine(_workspace.SamplesPath, imageFile);
        var metadataPath = Path.Combine(_workspace.SamplesPath, id + ".json");
        WriteAtomically(imagePath, png);
        WriteAtomically(metadataPath, JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions));
        return record;
    }

    internal IReadOnlyList<TrainingSampleRecord> List()
    {
        if (!Directory.Exists(_workspace.SamplesPath))
            return [];

        string[] paths;
        try
        {
            paths = Directory.GetFiles(_workspace.SamplesPath, "*.json");
        }
        catch (Exception exception) when (TrainingWorkspace.IsTransientFileSystemError(exception))
        {
            return [];
        }

        var samples = new List<TrainingSampleRecord>();
        foreach (var path in paths)
        {
            try
            {
                samples.Add(Load(path));
            }
            catch (Exception exception) when (TrainingWorkspace.IsTransientFileSystemError(exception))
            {
                // A workspace swap may remove a sample after enumeration. The next push retries it.
            }
        }
        return samples.OrderBy(sample => sample.Id, StringComparer.Ordinal).ToList();
    }

    internal void RemoveDatasetBackedSamples()
    {
        foreach (var sample in List().Where(sample => sample.DatasetImagePath is not null).ToList())
            Delete(sample.Id);
    }

    internal TrainingSampleRecord LoadById(string id)
    {
        var path = MetadataPath(id);
        if (!File.Exists(path))
            throw new FileNotFoundException("Training sample not found.", path);
        return Load(path);
    }

    internal TrainingSampleRecord UpdateLabels(string id, IReadOnlyList<TrainingLabel> labels,
        IReadOnlyList<EventDefinition> definitions,
        IReadOnlyList<TrainingRegionGroup>? regionGroups = null,
        IReadOnlyList<TrainingOcrRegion>? ocrRegions = null)
    {
        var records = List().ToList();
        var record = records.FirstOrDefault(candidate => candidate.Id == id)
            ?? throw new FileNotFoundException("Training sample not found.", MetadataPath(id));
        var definitionsByClass = definitions
            .Where(definition => definition.DetectionKind == DetectionKind.Object)
            .ToDictionary(definition => definition.ClassId);
        var fixedUpdates = new Dictionary<int, TrainingLabel>();
        foreach (var group in labels.GroupBy(label => label.ClassId))
        {
            if (!definitionsByClass.TryGetValue(group.Key, out var definition)
                || !definition.FixedPosition)
                continue;
            var fixedLabel = group.SingleOrDefault()
                ?? throw new InvalidDataException(
                    $"A sample cannot contain multiple fixed-position '{definition.Name}' labels.");
            fixedUpdates[group.Key] = fixedLabel;
            definition.FixedLabelCenterX = fixedLabel.CenterX;
            definition.FixedLabelCenterY = fixedLabel.CenterY;
            definition.FixedLabelWidth = fixedLabel.Width;
            definition.FixedLabelHeight = fixedLabel.Height;
        }

        record.Labels = labels.Select(Clone).ToList();
        if (ocrRegions is not null)
            record.OcrRegions = ocrRegions.Select(Clone).ToList();
        foreach (var other in records.Where(candidate => candidate.Id != id))
        {
            foreach (var label in other.Labels)
            {
                if (!fixedUpdates.TryGetValue(label.ClassId, out var fixedLabel)) continue;
                label.CenterX = fixedLabel.CenterX;
                label.CenterY = fixedLabel.CenterY;
                label.Width = fixedLabel.Width;
                label.Height = fixedLabel.Height;
            }
        }

        var changedRecords = records.Where(candidate => candidate.Id == id
            || candidate.Labels.Any(label => fixedUpdates.ContainsKey(label.ClassId))).ToList();
        foreach (var candidate in changedRecords)
        {
            var labelError = TrainingLabelValidator.FindBlockingError(candidate.Labels, definitions);
            if (labelError is not null)
                throw new InvalidDataException($"Invalid training sample: {labelError}.");
            var regionError = TrainingOcrRegionValidator.FindError(candidate.OcrRegions);
            if (regionError is not null)
                throw new InvalidDataException($"Invalid training sample: {regionError}.");
        }
        TrainingEventValidator.ValidateFixedPositions(definitions);

        var originals = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        var updates = new List<(string Path, byte[] Contents)>();
        foreach (var candidate in changedRecords)
        {
            var path = MetadataPath(candidate.Id);
            originals[path] = File.Exists(path) ? File.ReadAllBytes(path) : null;
            updates.Add((path, JsonSerializer.SerializeToUtf8Bytes(candidate, JsonOptions)));
            var datasetLabelPath = DatasetLabelPath(candidate);
            if (datasetLabelPath is null) continue;
            originals[datasetLabelPath] = File.Exists(datasetLabelPath)
                ? File.ReadAllBytes(datasetLabelPath) : null;
            updates.Add((datasetLabelPath, SerializeLabels(candidate.Labels)));
        }
        if (fixedUpdates.Count > 0)
        {
            originals[_workspace.EventsPath] = File.Exists(_workspace.EventsPath)
                ? File.ReadAllBytes(_workspace.EventsPath) : null;
            updates.Add((_workspace.EventsPath,
                JsonSerializer.SerializeToUtf8Bytes(definitions, JsonOptions)));
        }
        try
        {
            foreach (var (path, contents) in updates)
                WriteAtomically(path, contents);
            return record;
        }
        catch
        {
            RestoreMetadata(originals);
            throw;
        }
    }

    internal sealed record RemapClassIdsResult(
        IReadOnlyDictionary<string, byte[]?> Originals,
        int RemovedLabelCount);

    // Remaps surviving event class ids. When an event is deleted (its class missing from the
    // mapping) its labels are stripped from every sample in the same transaction rather than
    // rejecting the delete; dataset-backed label files stay in step. OCR regions carry no event
    // reference, so a remap never touches them.
    internal RemapClassIdsResult RemapClassIds(IReadOnlyDictionary<int, int> mapping,
        IReadOnlyDictionary<int, TrainingLabel>? fixedPositions = null,
        Action<int, int>? progress = null)
    {
        var records = List().ToList();
        var originals = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        var updates = new List<(string Path, byte[] Contents)>();
        var removedLabelCount = 0;
        for (var recordIndex = 0; recordIndex < records.Count; recordIndex++)
        {
            progress?.Invoke(recordIndex + 1, records.Count);
            var record = records[recordIndex];
            var removed = record.Labels.Count(label => !mapping.ContainsKey(label.ClassId));
            removedLabelCount += removed;
            var nextLabels = record.Labels.Where(label => mapping.ContainsKey(label.ClassId)).ToList();
            var changed = removed > 0;
            foreach (var label in nextLabels)
            {
                var previousClassId = label.ClassId;
                var nextClassId = mapping[previousClassId];
                if (label.ClassId != nextClassId)
                {
                    label.ClassId = nextClassId;
                    changed = true;
                }
                if (fixedPositions is not null
                    && fixedPositions.TryGetValue(previousClassId, out var fixedPosition)
                    && (label.CenterX != fixedPosition.CenterX
                        || label.CenterY != fixedPosition.CenterY
                        || label.Width != fixedPosition.Width
                        || label.Height != fixedPosition.Height))
                {
                    label.CenterX = fixedPosition.CenterX;
                    label.CenterY = fixedPosition.CenterY;
                    label.Width = fixedPosition.Width;
                    label.Height = fixedPosition.Height;
                    changed = true;
                }
            }
            record.Labels = nextLabels;
            if (!changed) continue;

            var path = MetadataPath(record.Id);
            originals[path] = File.ReadAllBytes(path);
            updates.Add((path, JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions)));
            var datasetLabelPath = DatasetLabelPath(record);
            if (datasetLabelPath is not null)
            {
                originals.TryAdd(datasetLabelPath,
                    File.Exists(datasetLabelPath) ? File.ReadAllBytes(datasetLabelPath) : null);
                updates.Add((datasetLabelPath, SerializeLabels(record.Labels)));
            }
        }

        try
        {
            foreach (var (path, contents) in updates)
                WriteAtomically(path, contents);
        }
        catch
        {
            RestoreMetadata(originals);
            throw;
        }

        return new RemapClassIdsResult(originals, removedLabelCount);
    }

    internal void RestoreMetadata(IReadOnlyDictionary<string, byte[]?> originals)
    {
        foreach (var (path, contents) in originals)
        {
            if (contents is null)
            {
                if (File.Exists(path)) File.Delete(path);
            }
            else
            {
                WriteAtomically(path, contents);
            }
        }
    }

    internal void Delete(string id)
    {
        var record = LoadById(id);
        File.Delete(MetadataPath(record.Id));
        var imagePath = Path.Combine(_workspace.SamplesPath, record.ImageFile);
            if (File.Exists(imagePath))
                File.Delete(imagePath);
            var datasetLabelPath = DatasetLabelPath(record);
            if (record.DatasetImagePath is not null)
            {
                var datasetImagePath = DatasetImagePath(record);
                if (File.Exists(datasetImagePath)) File.Delete(datasetImagePath);
                if (datasetLabelPath is not null && File.Exists(datasetLabelPath)) File.Delete(datasetLabelPath);
            }
    }

    internal static string SampleId(string sourcePath, double timestampSeconds)
    {
        var identity = Path.GetFullPath(sourcePath) + "\0" +
            timestampSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24].ToLowerInvariant();
    }

    private static TrainingLabel Clone(TrainingLabel label) => new()
    {
        ClassId = label.ClassId,
        CenterX = label.CenterX,
        CenterY = label.CenterY,
        Width = label.Width,
        Height = label.Height,
    };

    private static TrainingOcrRegion Clone(TrainingOcrRegion region) => new()
    {
        X = region.X,
        Y = region.Y,
        Width = region.Width,
        Height = region.Height,
        Text = region.Text,
    };

    private TrainingSampleRecord Load(string path) =>
        JsonSerializer.Deserialize<TrainingSampleRecord>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"Training sample metadata is empty: {path}");

    private string MetadataPath(string id)
    {
        if (id.Length != 24 || id.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("The training sample id is invalid.", nameof(id));
        return Path.Combine(_workspace.SamplesPath, id.ToLowerInvariant() + ".json");
    }

    private string? DatasetLabelPath(TrainingSampleRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.DatasetImagePath))
            return null;
        var relative = record.DatasetImagePath.Replace('/', Path.DirectorySeparatorChar);
        if (!relative.StartsWith("images" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The imported dataset image path is invalid.");
        var labelRelative = "labels" + relative["images".Length..];
        labelRelative = Path.ChangeExtension(labelRelative, ".txt");
        var fullPath = Path.GetFullPath(Path.Combine(_workspace.DatasetPath, labelRelative));
        var root = Path.GetFullPath(_workspace.DatasetPath).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The imported dataset label path is outside the workspace.");
        return fullPath;
    }

    private string DatasetImagePath(TrainingSampleRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.DatasetImagePath))
            throw new InvalidDataException("The imported dataset image path is missing.");
        var relative = record.DatasetImagePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(_workspace.DatasetPath, relative));
        var root = Path.GetFullPath(_workspace.DatasetPath).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The imported dataset image path is outside the workspace.");
        return fullPath;
    }

    private static byte[] SerializeLabels(IReadOnlyList<TrainingLabel> labels)
    {
        var text = string.Join(Environment.NewLine, labels.Select(label => string.Join(" ",
            label.ClassId.ToString(CultureInfo.InvariantCulture),
            label.CenterX.ToString("R", CultureInfo.InvariantCulture),
            label.CenterY.ToString("R", CultureInfo.InvariantCulture),
            label.Width.ToString("R", CultureInfo.InvariantCulture),
            label.Height.ToString("R", CultureInfo.InvariantCulture)))) + Environment.NewLine;
        return Encoding.UTF8.GetBytes(text);
    }

    internal static void WriteAtomically(string destinationPath, ReadOnlySpan<byte> bytes)
    {
        var temporaryPath = destinationPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporaryPath, bytes.ToArray());
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}

#endif
