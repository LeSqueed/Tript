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

internal sealed class TrainingLabelSuggestion
{
    public TrainingLabel Label { get; init; } = new();

    public float Confidence { get; init; }
}

internal static class TrainingLabelSuggestionFilter
{
    private const float OverlapIouThreshold = 0.3f;

    internal static List<TrainingLabelSuggestion> Merge(
        IReadOnlyList<TrainingLabel> existing, IReadOnlyList<DetectionResult> detections)
    {
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

            var box = ToBox(detection);
            if (accepted.Any(existingBox => IoU(existingBox, box) >= OverlapIouThreshold))
                continue;

            accepted.Add(box);
            suggestions.Add(new TrainingLabelSuggestion
            {
                Label = new TrainingLabel
                {
                    ClassId = detection.ClassId,
                    CenterX = detection.X + detection.Width / 2,
                    CenterY = detection.Y + detection.Height / 2,
                    Width = detection.Width,
                    Height = detection.Height,
                },
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

    // Imported dataset images are already cropped to the model input. Keep them in the imported
    // dataset instead of sending them through the full-frame crop exporter a second time.
    public string? DatasetImagePath { get; set; }
}

internal static class TrainingLabelValidator
{
    internal static string? FindError(IReadOnlyList<TrainingLabel> labels,
        IReadOnlyList<EventDefinition> definitions, bool requireLabel = true)
    {
        if (requireLabel && labels.Count == 0)
            return "a sample must contain at least one label";

        var classIds = definitions.Select(definition => definition.ClassId).ToHashSet();
        for (var index = 0; index < labels.Count; index++)
        {
            var label = labels[index];
            if (!classIds.Contains(label.ClassId))
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
        _workspace.EnsureDirectories();
    }

    internal TrainingSampleRecord Save(string sourcePath, double timestampSeconds, int imageWidth,
        int imageHeight, IReadOnlyList<TrainingLabel> labels, ReadOnlySpan<byte> png,
        IReadOnlyList<EventDefinition> definitions, string? datasetImagePath = null)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("A sample requires its source video path.", nameof(sourcePath));
        if (!double.IsFinite(timestampSeconds) || timestampSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(timestampSeconds));
        if (imageWidth <= 0 || imageHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(imageWidth), "Image dimensions must be positive.");

        // Captured frames start unlabeled and are completed in the sample editor. Dataset export
        // still requires labels before a sample can be used for training.
        var labelError = TrainingLabelValidator.FindError(labels, definitions, requireLabel: false);
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

        return Directory.EnumerateFiles(_workspace.SamplesPath, "*.json")
            .Select(path => Load(path))
            .OrderBy(sample => sample.Id, StringComparer.Ordinal)
            .ToList();
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
        IReadOnlyList<EventDefinition> definitions)
    {
        var path = MetadataPath(id);
        var record = LoadById(id);
        var labelError = TrainingLabelValidator.FindError(labels, definitions, requireLabel: false);
        if (labelError is not null)
            throw new InvalidDataException($"Invalid training sample: {labelError}.");

        record.Labels = labels.Select(Clone).ToList();
        var datasetLabelPath = DatasetLabelPath(record);
        var previousDatasetLabels = datasetLabelPath is not null && File.Exists(datasetLabelPath)
            ? File.ReadAllBytes(datasetLabelPath)
            : null;
        try
        {
            if (datasetLabelPath is not null)
                WriteAtomically(datasetLabelPath, SerializeLabels(record.Labels));
            WriteAtomically(path, JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions));
            return record;
        }
        catch
        {
            if (datasetLabelPath is not null)
            {
                if (previousDatasetLabels is null && File.Exists(datasetLabelPath))
                    File.Delete(datasetLabelPath);
                else if (previousDatasetLabels is not null)
                    WriteAtomically(datasetLabelPath, previousDatasetLabels);
            }
            throw;
        }
    }

    internal IReadOnlyDictionary<string, byte[]?> RemapClassIds(IReadOnlyDictionary<int, int> mapping)
    {
        var records = List().ToList();
        if (records.SelectMany(record => record.Labels).Any(label => !mapping.ContainsKey(label.ClassId)))
            throw new InvalidDataException("An event with labeled samples cannot be deleted.");

        var originals = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        var updates = new List<(string Path, byte[] Contents)>();
        foreach (var record in records)
        {
            var changed = false;
            foreach (var label in record.Labels)
            {
                var nextClassId = mapping[label.ClassId];
                if (label.ClassId != nextClassId)
                {
                    label.ClassId = nextClassId;
                    changed = true;
                }
            }

            if (changed)
            {
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

        return originals;
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
