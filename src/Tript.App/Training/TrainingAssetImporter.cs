// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

#if TRIPT_TRAINING

using System.Text.Json;
using System.Buffers.Binary;
using System.Globalization;
using Tript.Detection;

namespace Tript.App.Training;

internal sealed record TrainingImportResult(
    string GameId,
    string SourcePath,
    string WorkspacePath,
    bool ModelImported,
    int EventCount,
    int SampleCount,
    int TrainingImageCount,
    int ValidationImageCount,
    IReadOnlyList<string> Warnings,
    OnnxModelMetadata? ModelMetadata);

internal static class TrainingAssetImporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal static TrainingImportResult Import(string sourcePath, string gameId, string? rootPath = null)
    {
        var sourceRoot = ResolveSourceRoot(sourcePath);
        var eventsPath = Path.Combine(sourceRoot, "events.json");
        if (!File.Exists(eventsPath))
            throw new FileNotFoundException("The training source has no events.json.", eventsPath);

        var definitions = JsonSerializer.Deserialize<List<EventDefinition>>(
            File.ReadAllText(eventsPath), JsonOptions) ?? [];
        if (definitions.Count == 0)
            throw new InvalidDataException("events.json contains no event definitions.");
        TrainingEventValidator.ValidateRegions(definitions);
        TrainingEventValidator.ValidateSubtractorReferences(definitions);
        var sourceGroupsPath = Path.Combine(sourceRoot, "regionGroups.json");
        var regionGroups = File.Exists(sourceGroupsPath)
            ? JsonSerializer.Deserialize<List<TrainingRegionGroup>>(
                File.ReadAllText(sourceGroupsPath), JsonOptions) ?? []
            : [];
        TrainingEventValidator.ValidateRegionGroups(regionGroups);
        TrainingEventValidator.ValidateRegionGroupReferences(definitions, regionGroups);

        var sourceModelPath = Path.Combine(sourceRoot, "model.onnx");
        OnnxModelMetadata? modelMetadata = null;
        var warnings = new List<string>();
        if (File.Exists(sourceModelPath))
        {
            modelMetadata = OnnxModelInspector.Inspect(sourceModelPath);
            var mismatch = ModelApiV1Compatibility.FindMismatch(definitions, modelMetadata);
            if (mismatch is not null)
                throw new InvalidDataException($"The model and events.json do not match: {mismatch}");
        }
        else
        {
            warnings.Add("No model.onnx was imported; this workspace has a provisional event schema.");
        }

        var importedSamples = LoadSourceSamples(sourceRoot, definitions);
        if (importedSamples.Count == 0)
            throw new InvalidDataException("The selected training workspace contains no full-frame samples.");

        var workspace = TrainingWorkspace.ForGame(gameId, rootPath);
        var existingWorkspace = Directory.Exists(workspace.RootPath);

        var stagingPath = workspace.RootPath + ".import-" + Guid.NewGuid().ToString("N");
        var staging = TrainingWorkspace.AtRoot(gameId, stagingPath);
        string? backupPath = null;
        try
        {
            if (existingWorkspace)
                CopyDirectory(workspace.RootPath, staging.RootPath);
            else
                Directory.CreateDirectory(staging.RootPath);

            File.Copy(eventsPath, staging.EventsPath, overwrite: true);
            if (regionGroups.Count > 0)
                File.Copy(sourceGroupsPath, staging.RegionGroupsPath, overwrite: true);
            else if (File.Exists(staging.RegionGroupsPath))
                File.Delete(staging.RegionGroupsPath);
            if (modelMetadata is not null)
                File.Copy(sourceModelPath, staging.ModelPath, overwrite: true);
            else if (File.Exists(staging.ModelPath))
                File.Delete(staging.ModelPath);

            var sampleStore = new TrainingSampleStore(staging);
            sampleStore.RemoveDatasetBackedSamples();
            if (Directory.Exists(staging.DatasetPath))
                Directory.Delete(staging.DatasetPath, recursive: true);
            Directory.CreateDirectory(staging.DatasetPath);
            foreach (var sample in importedSamples)
            {
                sampleStore.Save(sample.SourcePath, sample.TimestampSeconds, sample.Width, sample.Height, sample.Labels,
                    File.ReadAllBytes(sample.SourcePath), definitions, regionGroups: regionGroups);
            }
            if (existingWorkspace)
            {
                backupPath = workspace.RootPath + ".backup-" + Guid.NewGuid().ToString("N");
                Directory.Move(workspace.RootPath, backupPath);
            }

            Directory.Move(staging.RootPath, workspace.RootPath);
            if (backupPath is not null)
            {
                Directory.Delete(backupPath, recursive: true);
                backupPath = null;
            }

            return new TrainingImportResult(
                gameId,
                sourceRoot,
                workspace.RootPath,
                modelMetadata is not null,
                definitions.Count,
                importedSamples.Count,
                0,
                0,
                warnings,
                modelMetadata);
        }
        catch
        {
            if (Directory.Exists(staging.RootPath))
                Directory.Delete(staging.RootPath, recursive: true);
            if (backupPath is not null && !Directory.Exists(workspace.RootPath))
                Directory.Move(backupPath, workspace.RootPath);
            throw;
        }
    }

    private static void CopyDirectory(string sourceRoot, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, directory)));

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, file)), overwrite: true);
    }

    private static string ResolveSourceRoot(string sourcePath)
    {
        var fullPath = Path.GetFullPath(sourcePath.Trim().Trim('"'));
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Training source directory not found: {fullPath}");

        if (File.Exists(Path.Combine(fullPath, "events.json")))
            return fullPath;

        var candidates = Directory.EnumerateFiles(fullPath, "events.json", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .Where(directory => directory is not null)
            .Select(directory => directory!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return candidates.Count switch
        {
            1 => candidates[0],
            0 => throw new InvalidDataException(
                $"The selected folder contains no training events.json: {fullPath}"),
            _ => throw new InvalidDataException(
                "The selected folder contains multiple game workspaces; select one game folder."),
        };
    }

    private sealed record ImportedSample(string SourcePath, double TimestampSeconds, int Width, int Height,
        List<TrainingLabel> Labels);

    private static List<ImportedSample> LoadSourceSamples(string sourceRoot,
        IReadOnlyList<EventDefinition> definitions)
    {
        var sourceSamples = Path.Combine(sourceRoot, "samples");
        if (!Directory.Exists(sourceSamples))
            return [];

        var eventById = definitions.ToDictionary(definition => definition.Id);
        var images = Directory.EnumerateFiles(sourceSamples, "*.png", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var labels = Directory.EnumerateFiles(sourceSamples, "*.txt", SearchOption.TopDirectoryOnly)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var imported = new List<ImportedSample>();
        foreach (var imagePath in images)
        {
            var labelPath = Path.ChangeExtension(imagePath, ".txt");
            if (!labels.Contains(labelPath))
                throw new InvalidDataException($"The full-frame sample has no matching label file: {imagePath}");
            var timestampSeconds = ParseSampleFileName(imagePath, eventById);
            if (!TryReadPngDimensions(File.ReadAllBytes(imagePath), out var width, out var height))
                throw new InvalidDataException($"The full-frame sample is not a supported PNG: {imagePath}");
            imported.Add(new ImportedSample(imagePath, timestampSeconds, width, height,
                LoadSampleLabels(labelPath, eventById)));
        }

        var unmatchedLabels = Directory.EnumerateFiles(sourceSamples, "*.txt", SearchOption.TopDirectoryOnly)
            .Where(path => !File.Exists(Path.ChangeExtension(path, ".png")))
            .ToList();
        if (unmatchedLabels.Count > 0)
            throw new InvalidDataException($"The full-frame sample has no matching image file: {unmatchedLabels[0]}");
        return imported;
    }

    private static double ParseSampleFileName(string imagePath,
        IReadOnlyDictionary<int, EventDefinition> eventById)
    {
        var stem = Path.GetFileNameWithoutExtension(imagePath);
        var separator = stem.IndexOf('_');
        if (separator <= 0 || separator == stem.Length - 1
            || !int.TryParse(stem[..separator], NumberStyles.Integer, CultureInfo.InvariantCulture, out var eventId)
            || !long.TryParse(stem[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
            || ticks < 0)
        {
            throw new InvalidDataException($"The full-frame sample filename is invalid: {imagePath}");
        }
        if (!eventById.ContainsKey(eventId))
            throw new InvalidDataException($"The full-frame sample filename references unknown event id {eventId}: {imagePath}");

        // The source timestamp is an absolute tick value, while the editor field is video-relative.
        return 0;
    }

    private static List<TrainingLabel> LoadSampleLabels(string path,
        IReadOnlyDictionary<int, EventDefinition> eventById)
    {
        var labels = new List<TrainingLabel>();
        foreach (var line in File.ReadLines(path))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            if (parts.Length != 5
                || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var eventId)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var centerX)
                || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var centerY)
                || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
                || !double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var height))
            {
                throw new InvalidDataException($"The full-frame sample label file is invalid: {path}");
            }
            if (!eventById.TryGetValue(eventId, out var definition))
                throw new InvalidDataException($"The full-frame sample label references unknown event id {eventId}: {path}");
            var left = Math.Max(0, centerX - width / 2);
            var top = Math.Max(0, centerY - height / 2);
            var right = Math.Min(1, centerX + width / 2);
            var bottom = Math.Min(1, centerY + height / 2);
            if (right <= left || bottom <= top)
                throw new InvalidDataException($"The full-frame sample label lies outside the image: {path}");
            labels.Add(new TrainingLabel
            {
                ClassId = definition.ClassId,
                CenterX = (left + right) / 2,
                CenterY = (top + bottom) / 2,
                Width = right - left,
                Height = bottom - top,
            });
        }
        return labels;
    }

    private static bool TryReadPngDimensions(byte[] bytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (bytes.Length < 24
            || bytes[0] != 137 || bytes[1] != 80 || bytes[2] != 78 || bytes[3] != 71
            || bytes[4] != 13 || bytes[5] != 10 || bytes[6] != 26 || bytes[7] != 10)
            return false;
        width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
        height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
        return width > 0 && height > 0;
    }

}

#endif
