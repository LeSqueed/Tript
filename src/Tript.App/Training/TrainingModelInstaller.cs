// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

#if TRIPT_TRAINING

using System.Text.Json;
using Tript.Detection;

namespace Tript.App.Training;

internal sealed record TrainingInstallResult(string GameId, string ModelPath, OnnxModelMetadata Metadata);

internal static class TrainingModelInstaller
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal static TrainingInstallResult Install(TrainingWorkspace workspace, string modelSourcePath)
    {
        if (!File.Exists(modelSourcePath))
            throw new FileNotFoundException("The trained model was not found.", modelSourcePath);
        if (!File.Exists(workspace.EventsPath))
            throw new FileNotFoundException("The workspace has no events.json.", workspace.EventsPath);

        var definitions = JsonSerializer.Deserialize<List<EventDefinition>>(
            File.ReadAllText(workspace.EventsPath), JsonOptions) ?? [];
        var metadata = OnnxModelInspector.Inspect(modelSourcePath);
        var mismatch = ModelApiV1Compatibility.FindMismatch(definitions, metadata);
        if (mismatch is not null)
            throw new InvalidDataException($"The model cannot be installed: {mismatch}");

        var targetWorkspace = TrainingWorkspace.ForGame(workspace.GameId, TrainingPaths.InstalledModelsPath);
        Directory.CreateDirectory(TrainingPaths.InstalledModelsPath);
        var stagingPath = targetWorkspace.RootPath + ".install-" + Guid.NewGuid().ToString("N");
        var backupPath = targetWorkspace.RootPath + ".backup-" + Guid.NewGuid().ToString("N");
        var staging = TrainingWorkspace.AtRoot(workspace.GameId, stagingPath);
        Directory.CreateDirectory(staging.RootPath);

        try
        {
            File.Copy(modelSourcePath, staging.ModelPath);
            File.Copy(workspace.EventsPath, staging.EventsPath);
            var stagedMetadata = OnnxModelInspector.Inspect(staging.ModelPath);
            var stagedMismatch = ModelApiV1Compatibility.FindMismatch(definitions, stagedMetadata);
            if (stagedMismatch is not null)
                throw new InvalidDataException($"The staged model cannot be installed: {stagedMismatch}");

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
                Path.Combine(targetWorkspace.RootPath, "model.onnx"), stagedMetadata);
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
