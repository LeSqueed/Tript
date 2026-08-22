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

        var sourceModelPath = Path.Combine(sourceRoot, "model.onnx");
        OnnxModelMetadata? modelMetadata = null;
        var warnings = new List<string>();
        if (File.Exists(sourceModelPath))
        {
            modelMetadata = OnnxModelInspector.Inspect(sourceModelPath);
            var mismatch = ModelEventCompatibility.FindMismatch(definitions, modelMetadata);
            if (mismatch is not null)
                throw new InvalidDataException($"The model and events.json do not match: {mismatch}");
        }
        else
        {
            warnings.Add("No model.onnx was imported; this workspace has a provisional event schema.");
        }

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
            if (modelMetadata is not null)
                File.Copy(sourceModelPath, staging.ModelPath, overwrite: true);
            else if (File.Exists(staging.ModelPath))
                File.Delete(staging.ModelPath);

            var counts = CopyDataset(sourceRoot, staging.DatasetPath);
            ImportDatasetSamples(sourceRoot, staging, definitions, warnings);
            var classIds = definitions.Select(definition => definition.ClassId).ToHashSet();
            var incompatibleSamples = new TrainingSampleStore(staging).List()
                .Count(sample => sample.Labels.Any(label => !classIds.Contains(label.ClassId)));
            if (incompatibleSamples > 0)
                warnings.Add($"{incompatibleSamples} preserved samples use class IDs not present in the imported event contract.");
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
                counts.Training,
                counts.Validation,
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

    private static (int Training, int Validation) CopyDataset(string sourceRoot, string destinationRoot)
    {
        var sourceDataset = Path.Combine(sourceRoot, "dataset");
        if (!Directory.Exists(sourceDataset))
            return (0, 0);

        var counts = new int[2];
        foreach (var (split, index) in new[] { ("train", 0), ("val", 1) })
        {
            var sourceImages = Path.Combine(sourceDataset, "images", split);
            var sourceLabels = Path.Combine(sourceDataset, "labels", split);
            if (!Directory.Exists(sourceImages))
                continue;

            var destinationImages = Path.Combine(destinationRoot, "images", split);
            var destinationLabels = Path.Combine(destinationRoot, "labels", split);
            Directory.CreateDirectory(destinationImages);
            Directory.CreateDirectory(destinationLabels);

            foreach (var imagePath in Directory.EnumerateFiles(sourceImages)
                         .Where(IsImageFile).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                    File.Copy(imagePath, Path.Combine(destinationImages, Path.GetFileName(imagePath)), overwrite: true);
                counts[index]++;
            }

            if (Directory.Exists(sourceLabels))
            {
                foreach (var labelPath in Directory.EnumerateFiles(sourceLabels, "*.txt")
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                        File.Copy(labelPath, Path.Combine(destinationLabels, Path.GetFileName(labelPath)), overwrite: true);
                }
            }
        }

        var yaml = Path.Combine(sourceDataset, "dataset.yaml");
        if (File.Exists(yaml))
            File.Copy(yaml, Path.Combine(destinationRoot, "dataset.yaml"), overwrite: true);

        return (counts[0], counts[1]);
    }

    private static void ImportDatasetSamples(string sourceRoot, TrainingWorkspace workspace,
        IReadOnlyList<EventDefinition> definitions, ICollection<string> warnings)
    {
        var store = new TrainingSampleStore(workspace);
        foreach (var (split, _) in new[] { ("train", 0), ("val", 1) })
        {
            var sourceImages = Path.Combine(sourceRoot, "dataset", "images", split);
            var sourceLabels = Path.Combine(sourceRoot, "dataset", "labels", split);
            if (!Directory.Exists(sourceImages)) continue;

            foreach (var imagePath in Directory.EnumerateFiles(sourceImages)
                         .Where(IsImageFile).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var labelPath = Path.Combine(sourceLabels, Path.GetFileNameWithoutExtension(imagePath) + ".txt");
                var labels = File.Exists(labelPath) ? LoadYoloLabels(labelPath) : [];
                var image = File.ReadAllBytes(imagePath);
                if (!TryReadPngDimensions(image, out var width, out var height))
                {
                    warnings.Add($"Skipped dataset sample with unsupported image data: {imagePath}");
                    continue;
                }
                var datasetImagePath = Path.Combine("images", split, Path.GetFileName(imagePath));
                store.Save(imagePath, 0, width, height, labels, image, definitions, datasetImagePath);
            }
        }
    }

    private static List<TrainingLabel> LoadYoloLabels(string path)
    {
        var labels = new List<TrainingLabel>();
        foreach (var line in File.ReadLines(path))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            if (parts.Length != 5 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var classId)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var centerX)
                || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var centerY)
                || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
                || !double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var height))
            {
                throw new InvalidDataException($"The dataset label file is invalid: {path}");
            }
            labels.Add(new TrainingLabel
            {
                ClassId = classId,
                CenterX = centerX,
                CenterY = centerY,
                Width = width,
                Height = height,
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

    private static bool IsImageFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg";
}

#endif
