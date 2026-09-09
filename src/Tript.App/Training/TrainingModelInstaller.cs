// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

#if TRIPT_TRAINING

using System.Text.Json;
using Tript.Detection;

namespace Tript.App.Training;

internal sealed record TrainingInstallResult(string GameId, string ModelPath, OnnxModelMetadata? Metadata);

internal static class TrainingModelInstaller
{
    internal static TrainingInstallResult Install(TrainingWorkspace workspace, string? modelSourcePath,
        string? ocrModelSourcePath = null, string? ocrDictionarySourcePath = null,
        string? ocrDetectorSourcePath = null)
    {
        if (!File.Exists(workspace.EventsPath))
            throw new FileNotFoundException("The workspace has no events.json.", workspace.EventsPath);

        var definitions = workspace.LoadDefinitions();
        var hasObjectEvents = definitions.Any(definition => definition.DetectionKind == DetectionKind.Object);
        var hasOcrEvents = definitions.Any(definition => definition.DetectionKind == DetectionKind.Ocr);
        if (hasOcrEvents)
        {
            ocrModelSourcePath ??= Path.Combine(workspace.DatasetPath, "ocr_model.onnx");
            ocrDictionarySourcePath ??= Path.Combine(workspace.DatasetPath, "ocr_dict.txt");
            if (File.Exists(workspace.OcrDetectorPath)) ocrDetectorSourcePath ??= workspace.OcrDetectorPath;
        }
        if (hasObjectEvents && (string.IsNullOrWhiteSpace(modelSourcePath) || !File.Exists(modelSourcePath)))
            throw new FileNotFoundException("The trained object model was not found.", modelSourcePath);
        if (hasOcrEvents && (string.IsNullOrWhiteSpace(ocrModelSourcePath) || !File.Exists(ocrModelSourcePath)))
            throw new FileNotFoundException("The trained OCR model was not found.", ocrModelSourcePath);
        if (hasOcrEvents && (string.IsNullOrWhiteSpace(ocrDictionarySourcePath)
            || !File.Exists(ocrDictionarySourcePath)))
            throw new FileNotFoundException("The trained OCR dictionary was not found.", ocrDictionarySourcePath);
        var regionGroups = workspace.LoadRegionGroups();
        TrainingEventValidator.ValidateRegionGroups(regionGroups);
        TrainingEventValidator.ValidateRegionGroupReferences(definitions, regionGroups);
        var runtimeDefinitions = TrainingRegionResolver.MaterializeEffectiveRegions(definitions,
            regionGroups);
        OnnxModelMetadata? metadata = null;
        if (hasObjectEvents)
        {
            metadata = OnnxModelInspector.Inspect(modelSourcePath!);
            var mismatch = ModelApiV1Compatibility.FindMismatch(definitions, metadata);
            if (mismatch is not null)
                throw new InvalidDataException($"The model cannot be installed: {mismatch}");
        }

        var targetWorkspace = TrainingWorkspace.ForGame(workspace.GameId, TrainingPaths.InstalledModelsPath);
        Directory.CreateDirectory(TrainingPaths.InstalledModelsPath);
        var stagingPath = targetWorkspace.RootPath + ".install-" + Guid.NewGuid().ToString("N");
        var backupPath = targetWorkspace.RootPath + ".backup-" + Guid.NewGuid().ToString("N");
        var staging = TrainingWorkspace.AtRoot(workspace.GameId, stagingPath);
        Directory.CreateDirectory(staging.RootPath);

        try
        {
            if (hasObjectEvents) File.Copy(modelSourcePath!, staging.ModelPath);
            if (hasOcrEvents)
            {
                File.Copy(ocrModelSourcePath!, Path.Combine(staging.RootPath, "ocr_model.onnx"));
                File.Copy(ocrDictionarySourcePath!, Path.Combine(staging.RootPath, "ocr_dict.txt"));
                if (!string.IsNullOrWhiteSpace(ocrDetectorSourcePath) && File.Exists(ocrDetectorSourcePath))
                    File.Copy(ocrDetectorSourcePath, Path.Combine(staging.RootPath, "ocr_detector.onnx"));
            }
            TrainingSampleStore.WriteAtomically(staging.EventsPath,
                JsonSerializer.SerializeToUtf8Bytes(runtimeDefinitions,
                    TrainingRegionResolver.WriteJsonOptions));
            if (regionGroups.Count > 0)
            {
                TrainingSampleStore.WriteAtomically(staging.RegionGroupsPath,
                    JsonSerializer.SerializeToUtf8Bytes(regionGroups,
                        TrainingRegionResolver.WriteJsonOptions));
            }
            OnnxModelMetadata? stagedMetadata = null;
            if (hasObjectEvents)
            {
                stagedMetadata = OnnxModelInspector.Inspect(staging.ModelPath);
                var stagedMismatch = ModelApiV1Compatibility.FindMismatch(definitions, stagedMetadata);
                if (stagedMismatch is not null)
                    throw new InvalidDataException($"The staged model cannot be installed: {stagedMismatch}");
            }

            var hadPrevious = Directory.Exists(targetWorkspace.RootPath);
            if (hadPrevious)
                Directory.Move(targetWorkspace.RootPath, backupPath);

            try
            {
                Directory.Move(staging.RootPath, targetWorkspace.RootPath);
            }
            catch
            {
                if (hadPrevious && Directory.Exists(backupPath) && !Directory.Exists(targetWorkspace.RootPath))
                    Directory.Move(backupPath, targetWorkspace.RootPath);
                throw;
            }

            if (Directory.Exists(backupPath))
                Directory.Delete(backupPath, recursive: true);

            return new TrainingInstallResult(workspace.GameId,
                Path.Combine(targetWorkspace.RootPath, hasObjectEvents ? "model.onnx" : "ocr_model.onnx"),
                stagedMetadata);
        }
        catch
        {
            if (Directory.Exists(staging.RootPath))
                Directory.Delete(staging.RootPath, recursive: true);
            if (Directory.Exists(backupPath) && !Directory.Exists(targetWorkspace.RootPath))
                Directory.Move(backupPath, targetWorkspace.RootPath);
            throw;
        }
    }
}

#endif
