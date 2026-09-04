// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

#if TRIPT_TRAINING
using System.Text.Json;
using Serilog;
using Tript.App.Content;
using Tript.App.Ipc;
using Tript.App.Training;
using Tript.Detection;
using Tript.Media;

namespace Tript.App;

internal sealed partial class AppHost
{
    private readonly TrainingRunner _trainingRunner = new();
    private readonly object _trainingGate = new();
    private readonly SemaphoreSlim _trainingWorkspaceGate = new(1, 1);
    private CancellationTokenSource? _trainingCancellation;
    private string? _trainingGameId;

    private async Task WithTrainingWorkspaceLockAsync(Func<Task> action)
    {
        await _trainingWorkspaceGate.WaitAsync();
        try
        {
            await action();
        }
        finally
        {
            _trainingWorkspaceGate.Release();
        }
    }

    internal TrainingInstallResult InstallTrainingModel(string gameId, string modelSourcePath)
    {
        lock (_recorderGate)
        {
            var restart = IsRecording && string.Equals(_currentGameId, gameId, StringComparison.OrdinalIgnoreCase);
            if (restart)
                StopDetection();

            try
            {
                ModelService.InvalidateModel(gameId);
                var result = TrainingModelInstaller.Install(TrainingWorkspace.ForGame(gameId), modelSourcePath);
                if (restart)
                    StartDetection(gameId);
                return result;
            }
            catch
            {
                // The old model files were not touched until the staged install committed.
                if (restart)
                    StartDetection(gameId);
                throw;
            }
        }
    }

    private TrainingWorkspace EnsureTrainingWorkspace(string gameId)
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
        }
        return workspace;
    }

    internal async Task PushTraining(string? requestedGameId)
        => await WithTrainingWorkspaceLockAsync(() =>
        {
            PushTrainingCore(requestedGameId);
            return Task.CompletedTask;
        });

    private void PushTrainingCore(string? requestedGameId)
    {
        var gameId = requestedGameId ?? GameList.FirstOrDefault()?.Id;
        if (string.IsNullOrWhiteSpace(gameId))
        {
            _ipc.Broadcast("training", JsonSerializer.SerializeToElement(new
            {
                training = new { gameId = (string?)null, events = Array.Empty<object>(), samples = Array.Empty<object>() },
            }, Wire.Options));
            return;
        }

        var workspace = EnsureTrainingWorkspace(gameId);
        var definitions = workspace.LoadDefinitions();
        var regionGroups = workspace.LoadRegionGroups();
        var samples = new TrainingSampleStore(workspace).List();
        var invalidSamples = samples.Select(sample => new
        {
            sample.Id,
            Reason = TrainingLabelValidator.FindError(sample.Labels, definitions,
                requireLabel: true, regionGroups: regionGroups),
        }).Where(sample => sample.Reason is not null).ToList();
        TrainingDatasetExportSummary? exportSummary = null;
        try
        {
            exportSummary = TrainingRunner.LoadExportSummary(workspace);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Training: could not read dataset coverage for {GameId}", gameId);
        }
        var dataset = new
        {
            trainingImages = CountTrainingImages(workspace.DatasetPath, "train"),
            validationImages = CountTrainingImages(workspace.DatasetPath, "val"),
            eventCoverage = exportSummary?.EventCoverage ?? [],
            warnings = exportSummary?.Warnings ?? [],
        };
        OnnxModelMetadata? metadata = null;
        // The installed runtime model is the one the recorder actually uses and is always refreshed
        // on import/train/install; prefer it over the workspace's own copy so a stale imported model
        // on the workspace can never be described as the active one.
        var runtimeModelPath = ModelService.GetModelPath(workspace.GameId);
        var modelPath = File.Exists(runtimeModelPath) ? runtimeModelPath : workspace.ModelPath;
        if (File.Exists(modelPath))
        {
            try
            {
                metadata = OnnxModelInspector.Inspect(modelPath);
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "Training: could not inspect the model for {GameId}", gameId);
            }
        }

        _ipc.Broadcast("training", JsonSerializer.SerializeToElement(new
        {
            training = new
            {
                gameId,
                revision = workspace.Revision(),
                events = definitions,
                regionGroups,
                samples,
                invalidSamples,
                dataset,
                preferences = workspace.LoadPreferences(),
                model = metadata is null ? null : new
                {
                    inputWidth = metadata.InputWidth,
                    inputHeight = metadata.InputHeight,
                    classCount = metadata.ClassCount,
                    classNames = metadata.ClassNames,
                },
                trainingActive = string.Equals(_trainingGameId, gameId, StringComparison.OrdinalIgnoreCase),
            },
        }, Wire.Options));
    }

    internal async Task ImportTrainingAssets(ImportTrainingParameters? parameters)
        => await WithTrainingWorkspaceLockAsync(() =>
        {
            ImportTrainingAssetsCore(parameters);
            return Task.CompletedTask;
        });

    private void ImportTrainingAssetsCore(ImportTrainingParameters? parameters)
    {
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.GameId))
            throw new ArgumentException("A game id is required to import training assets.");

        lock (_trainingGate)
        {
            if (_trainingCancellation is not null)
                throw new InvalidOperationException("Stop training before importing a workspace.");
        }

        var existingWorkspace = TrainingWorkspace.ForGame(parameters.GameId);
        var currentRevision = existingWorkspace.Revision();
        if (!string.Equals(parameters.ExpectedRevision ?? string.Empty, currentRevision, StringComparison.Ordinal))
            throw new InvalidOperationException("The training workspace changed. Refresh it and confirm the import again.");
        var hasExistingData = Directory.Exists(existingWorkspace.RootPath)
            && (File.Exists(existingWorkspace.EventsPath)
                || (Directory.Exists(existingWorkspace.SamplesPath)
                    && Directory.EnumerateFiles(existingWorkspace.SamplesPath, "*", SearchOption.AllDirectories).Any())
                || (Directory.Exists(existingWorkspace.DatasetPath)
                    && Directory.EnumerateFiles(existingWorkspace.DatasetPath, "*", SearchOption.AllDirectories).Any()));
        if (hasExistingData && !parameters.ConfirmOverwrite)
            throw new InvalidOperationException("This training workspace already contains data. Confirm overwrite before importing.");

        var result = TrainingAssetImporter.Import(parameters.SourcePath, parameters.GameId);
        var importMessage = $"Imported {result.EventCount} events and {result.SampleCount} full-frame samples. Prepared dataset images were not imported.";
        if (result.Warnings.Count > 0)
            importMessage += " Warnings: " + string.Join(" ", result.Warnings);
        PushTrainingProgress(parameters.GameId, "imported",
            importMessage);
        if (result.ModelImported)
            InstallTrainingModel(parameters.GameId, TrainingWorkspace.ForGame(parameters.GameId).ModelPath);
        else
            RemoveInstalledTrainingModel(parameters.GameId);
        PushTrainingCore(parameters.GameId);
    }

    private void RemoveInstalledTrainingModel(string gameId)
    {
        lock (_recorderGate)
        {
            var restart = IsRecording && string.Equals(_currentGameId, gameId, StringComparison.OrdinalIgnoreCase);
            if (restart)
                StopDetection();
            try
            {
                ModelService.InvalidateModel(gameId);
                var installedRoot = TrainingWorkspace.ForGame(gameId, TrainingPaths.InstalledModelsPath).RootPath;
                if (Directory.Exists(installedRoot))
                    Directory.Delete(installedRoot, recursive: true);
                if (restart)
                    StartDetection(gameId);
            }
            catch
            {
                if (restart)
                    StartDetection(gameId);
                throw;
            }
        }
    }

    internal async Task CaptureTrainingSample(CaptureTrainingSampleParameters? parameters)
        => await WithTrainingWorkspaceLockAsync(() => CaptureTrainingSampleCore(parameters));

    private async Task CaptureTrainingSampleCore(CaptureTrainingSampleParameters? parameters)
    {
        if (parameters is null)
            throw new ArgumentException("Training sample parameters are required.");
        var workspace = EnsureTrainingWorkspace(parameters.GameId);
        var sourcePath = ContentServer.ResolveWithinRoot(EffectiveRoot, parameters.FilePath)
            ?? throw new InvalidDataException("The sample source is not inside the recording folder.");
        var tools = _libraryTools.Value
            ?? throw new InvalidOperationException("FFmpeg and ffprobe are required to capture training samples.");
        var media = new MediaProbe(tools.Ffprobe).Probe(sourcePath);
        var definitions = workspace.LoadDefinitions();
        var regionGroups = workspace.LoadRegionGroups();
        var labels = parameters.Labels.Select(ToTrainingLabel).ToList();
        var id = TrainingSampleStore.SampleId(sourcePath, parameters.TimestampSeconds);
        var temporaryPath = Path.Combine(workspace.SamplesPath, id + ".capture-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            var extractor = new FfmpegTrainingFrameExtractor(tools.Ffmpeg);
            if (!extractor.TryExtract(sourcePath, parameters.TimestampSeconds, temporaryPath))
                throw new InvalidOperationException(
                    $"FFmpeg could not extract the selected video frame.{Environment.NewLine}{extractor.LastError}");

            var sample = new TrainingSampleStore(workspace).Save(sourcePath, parameters.TimestampSeconds,
                media.Width, media.Height, labels, File.ReadAllBytes(temporaryPath), definitions,
                regionGroups: regionGroups);
            PushTrainingProgress(parameters.GameId, "sampleSaved", sample.Id);
            await PushTrainingSampleAsync(parameters.GameId, workspace, sample);
            PushTrainingCore(parameters.GameId);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    internal async Task GetTrainingSample(TrainingSampleParameters? parameters)
        => await WithTrainingWorkspaceLockAsync(() => GetTrainingSampleCore(parameters));

    private async Task GetTrainingSampleCore(TrainingSampleParameters? parameters)
    {
        if (parameters is null)
            throw new ArgumentException("Training sample parameters are required.");
        var workspace = EnsureTrainingWorkspace(parameters.GameId);
        var store = new TrainingSampleStore(workspace);
        TrainingSampleRecord sample;
        try
        {
            sample = store.LoadById(parameters.SampleId);
        }
        catch (FileNotFoundException) when (parameters.PreviewOnly)
        {
            return;
        }
        var imagePath = Path.Combine(workspace.SamplesPath, sample.ImageFile);
        if (!File.Exists(imagePath))
        {
            if (parameters.PreviewOnly)
                return;
            throw new FileNotFoundException("The training sample image is missing.", imagePath);
        }

        await PushTrainingSampleAsync(parameters.GameId, workspace, sample, parameters.PreviewOnly
            ? "trainingSamplePreview"
            : "trainingSample", parameters.RequestId);
    }

    internal async Task UpdateTrainingEvents(UpdateTrainingEventsParameters? parameters)
        => await WithTrainingWorkspaceLockAsync(() =>
        {
            UpdateTrainingEventsCore(parameters);
            return Task.CompletedTask;
        });

    private void UpdateTrainingEventsCore(UpdateTrainingEventsParameters? parameters)
    {
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.GameId))
            throw new ArgumentException("A game id is required to update training events.");
        if (parameters.Events.Count == 0)
            throw new InvalidDataException("A training workspace requires at least one event.");
        if (parameters.Events.Any(eventDefinition => string.IsNullOrWhiteSpace(eventDefinition.Name)))
            throw new InvalidDataException("Every training event requires a name.");
        if (parameters.Events.Select(eventDefinition => eventDefinition.Id).Distinct().Count()
            != parameters.Events.Count)
        {
            throw new InvalidDataException("Training event ids must be unique.");
        }
        if (parameters.Events.Select(eventDefinition => eventDefinition.ClassId).Distinct().Count()
            != parameters.Events.Count)
        {
            throw new InvalidDataException("Training event class ids must be unique.");
        }

        var workspace = EnsureTrainingWorkspace(parameters.GameId);
        var orderedEvents = parameters.Events.OrderBy(eventDefinition => eventDefinition.ClassId).ToList();
        var regionGroups = workspace.LoadRegionGroups();
        var classIdMap = orderedEvents
            .Select((eventDefinition, index) => new { Old = eventDefinition.ClassId, New = index })
            .ToDictionary(pair => pair.Old, pair => pair.New);
        var sampleStore = new TrainingSampleStore(workspace);
        InitializeFixedPositions(orderedEvents, sampleStore.List());
        TrainingEventValidator.ValidateRegions(orderedEvents);
        TrainingEventValidator.ValidateRegionGroupReferences(orderedEvents, regionGroups);
        TrainingEventValidator.ValidateFixedPositions(orderedEvents);
        TrainingEventValidator.ValidateSubtractorReferences(orderedEvents);
        var lastReportedPercent = -1;
        var remap = sampleStore.RemapClassIds(classIdMap, (completed, total) =>
        {
            var percent = (int)Math.Floor(completed * 100.0 / Math.Max(1, total));
            if (percent == lastReportedPercent) return;
            lastReportedPercent = percent;
            PushTrainingProgress(parameters.GameId, "eventDeleteProgress",
                $"Removing labels from training samples… {percent}%", percent);
        });
        foreach (var eventDefinition in orderedEvents)
            eventDefinition.ClassId = classIdMap[eventDefinition.ClassId];
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
        var removedMessage = remap.RemovedLabelCount > 0
            ? $"Training events updated. Removed {remap.RemovedLabelCount} label"
                + $"{(remap.RemovedLabelCount == 1 ? string.Empty : "s")}."
            : "Training events updated.";
        PushTrainingProgress(parameters.GameId, "eventsUpdated", removedMessage);
        PushTrainingCore(parameters.GameId);
    }

    internal async Task UpdateTrainingRegionGroups(UpdateTrainingRegionGroupsParameters? parameters)
        => await WithTrainingWorkspaceLockAsync(() =>
        {
            UpdateTrainingRegionGroupsCore(parameters);
            return Task.CompletedTask;
        });

    private void UpdateTrainingRegionGroupsCore(UpdateTrainingRegionGroupsParameters? parameters)
    {
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.GameId))
            throw new ArgumentException("A game id is required to update training region groups.");

        TrainingEventValidator.ValidateRegionGroups(parameters.RegionGroups);
        var workspace = EnsureTrainingWorkspace(parameters.GameId);
        var events = workspace.LoadDefinitions();
        var groupIds = parameters.RegionGroups.Select(group => group.Id).ToHashSet();
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
            workspace.SaveRegionGroups(parameters.RegionGroups);
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
        PushTrainingProgress(parameters.GameId, "regionGroupsUpdated", "Training region groups updated.");
        PushTrainingCore(parameters.GameId);
    }

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

    internal async Task UpdateTrainingSample(UpdateTrainingSampleParameters? parameters)
        => await WithTrainingWorkspaceLockAsync(() =>
        {
            UpdateTrainingSampleCore(parameters);
            return Task.CompletedTask;
        });

    private void UpdateTrainingSampleCore(UpdateTrainingSampleParameters? parameters)
    {
        if (parameters is null)
            throw new ArgumentException("Training sample parameters are required.");
        var workspace = EnsureTrainingWorkspace(parameters.GameId);
        var labels = parameters.Labels.Select(ToTrainingLabel).ToList();
        var sample = new TrainingSampleStore(workspace).UpdateLabels(parameters.SampleId, labels,
            workspace.LoadDefinitions(), workspace.LoadRegionGroups());
        PushTrainingProgress(parameters.GameId, "sampleUpdated", sample.Id);
        PushTrainingCore(parameters.GameId);
    }

    internal async Task SuggestTrainingLabels(SuggestTrainingLabelsParameters? parameters)
        => await WithTrainingWorkspaceLockAsync(() =>
        {
            SuggestTrainingLabelsCore(parameters);
            return Task.CompletedTask;
        });

    private void SuggestTrainingLabelsCore(SuggestTrainingLabelsParameters? parameters)
    {
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.GameId))
            throw new ArgumentException("A game id is required to suggest training labels.");

        var workspace = EnsureTrainingWorkspace(parameters.GameId);
        var sample = new TrainingSampleStore(workspace).LoadById(parameters.SampleId);
        var imagePath = Path.Combine(workspace.SamplesPath, sample.ImageFile);
        if (!File.Exists(imagePath))
            throw new FileNotFoundException("The training sample image is missing.", imagePath);
        var modelPath = ModelService.GetModelPath(workspace.GameId);
        if (!File.Exists(modelPath))
            throw new InvalidOperationException("No live model is available for label suggestions.");

        var definitions = workspace.LoadDefinitions();
        var regionGroups = workspace.LoadRegionGroups();
        var detections = ModelPredictionService.Predict(modelPath,
            File.ReadAllBytes(imagePath),
            TrainingRegionResolver.MaterializeEffectiveRegions(definitions, regionGroups));
        var suggestions = TrainingLabelSuggestionFilter.Merge(sample.Labels, detections, definitions,
            regionGroups);
        _ipc.Broadcast("trainingLabelSuggestions", JsonSerializer.SerializeToElement(new
        {
            gameId = parameters.GameId,
            sampleId = parameters.SampleId,
            requestId = parameters.RequestId,
            suggestions,
        }, Wire.Options));
    }

    internal async Task DeleteTrainingSample(TrainingSampleParameters? parameters)
        => await WithTrainingWorkspaceLockAsync(() =>
        {
            DeleteTrainingSampleCore(parameters);
            return Task.CompletedTask;
        });

    private void DeleteTrainingSampleCore(TrainingSampleParameters? parameters)
    {
        if (parameters is null)
            throw new ArgumentException("Training sample parameters are required.");
        var workspace = EnsureTrainingWorkspace(parameters.GameId);
        new TrainingSampleStore(workspace).Delete(parameters.SampleId);
        PushTrainingProgress(parameters.GameId, "sampleDeleted", parameters.SampleId);
        PushTrainingCore(parameters.GameId);
    }

    internal async Task StartTraining(StartTrainingParameters? parameters)
        => await WithTrainingWorkspaceLockAsync(() =>
        {
            StartTrainingCore(parameters);
            return Task.CompletedTask;
        });

    private void StartTrainingCore(StartTrainingParameters? parameters)
    {
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.GameId))
            throw new ArgumentException("A game id is required to train a model.");

        var workspace = EnsureTrainingWorkspace(parameters.GameId);
        var imageSize = parameters.ImageSize;
        var currentModelPath = ResolveTrainingModelPath(
            ModelService.GetModelPath(workspace.GameId), workspace.ModelPath);
        if (imageSize is null && File.Exists(currentModelPath))
            imageSize = OnnxModelInspector.Inspect(currentModelPath).InputWidth;
        imageSize ??= 640;
        if ((parameters.AugmentCopies ?? 0) < 0)
            throw new ArgumentOutOfRangeException(nameof(parameters.AugmentCopies),
                "Augmented copies per crop must be non-negative.");

        // Remember these settings locally for the game so the training form restores them next
        // time this workspace is opened. The file stays in the workspace; it is never installed.
        workspace.SavePreferences(new TrainingPreferences
        {
            Epochs = parameters.Epochs,
            Device = parameters.Device,
            AugmentCopies = parameters.AugmentCopies ?? 0,
        });

        lock (_trainingGate)
        {
            if (_trainingCancellation is not null)
                throw new InvalidOperationException("Training is already running.");

            _trainingCancellation = new CancellationTokenSource();
            _trainingGameId = parameters.GameId;
            var cancellation = _trainingCancellation;
            _ = RunTrainingAsync(workspace, imageSize.Value, parameters.AugmentCopies ?? 0, parameters, cancellation);
        }
        var augmentMessage = (parameters.AugmentCopies ?? 0) > 0
            ? $" augmentation: {parameters.AugmentCopies} copies per training crop (validation unchanged);"
            : string.Empty;
        PushTrainingProgress(parameters.GameId, "started",
            $"Training is running in a console window. Requested device: {parameters.Device}; " +
            $"epochs: {parameters.Epochs}; input: {imageSize}x{imageSize};" + augmentMessage);
    }

    internal void CancelTraining()
    {
        lock (_trainingGate)
        {
            _trainingRunner.Cancel();
            _trainingCancellation?.Cancel();
        }
    }

    internal async Task InstallTrainingModelCommand(TrainingGameParameters? parameters)
        => await WithTrainingWorkspaceLockAsync(() =>
        {
            InstallTrainingModelCommandCore(parameters);
            return Task.CompletedTask;
        });

    private void InstallTrainingModelCommandCore(TrainingGameParameters? parameters)
    {
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.GameId))
            throw new ArgumentException("A game id is required to install a model.");
        var workspace = EnsureTrainingWorkspace(parameters.GameId);
        var source = Path.Combine(workspace.DatasetPath, "model.onnx");
        var result = InstallTrainingModel(parameters.GameId, source);
        RefreshWorkspaceModel(workspace, source);
        PushTrainingProgress(parameters.GameId, "completed", $"Installed {result.ModelPath}.");
        PushTrainingCore(parameters.GameId);
    }

    private async Task RunTrainingAsync(TrainingWorkspace workspace, int imageSize, int augmentCopies,
        StartTrainingParameters parameters, CancellationTokenSource cancellation)
    {
        await _trainingWorkspaceGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var result = await _trainingRunner.RunAsync(workspace, imageSize, parameters.Epochs,
                augmentCopies, parameters.Device, parameters.BaseModel,
                message => PushTrainingProgress(parameters.GameId, "progress", message),
                cancellation.Token).ConfigureAwait(false);
            var installed = InstallTrainingModel(parameters.GameId, result.DatasetModelPath);
            RefreshWorkspaceModel(workspace, result.DatasetModelPath);
            PushTrainingProgress(parameters.GameId, "completed", $"Installed {installed.ModelPath}.");
            PushTrainingCore(parameters.GameId);
        }
        catch (OperationCanceledException)
        {
            PushTrainingProgress(parameters.GameId, "cancelled", "Training was cancelled.");
        }
        catch (Exception exception)
        {
            PushTrainingProgress(parameters.GameId, "error", exception.Message);
        }
        finally
        {
            lock (_trainingGate)
            {
                if (ReferenceEquals(_trainingCancellation, cancellation))
                {
                    _trainingCancellation = null;
                    _trainingGameId = null;
                }
            }
            cancellation.Dispose();
            _trainingWorkspaceGate.Release();
            try
            {
                // The UI needs a final full state push so trainingActive becomes false after an error
                // or cancellation, not only after a successful model installation.
                PushTrainingCore(parameters.GameId);
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "Training: could not refresh workspace state after training ended");
            }
        }
    }

    private static TrainingLabel ToTrainingLabel(TrainingLabelParameters label) => new()
    {
        ClassId = label.ClassId,
        CenterX = label.CenterX,
        CenterY = label.CenterY,
        Width = label.Width,
        Height = label.Height,
    };

    private static int CountTrainingImages(string datasetPath, string split)
    {
        var path = Path.Combine(datasetPath, "images", split);
        return Directory.Exists(path)
            ? Directory.EnumerateFiles(path).Count(IsTrainingImage)
            : 0;
    }

    private static bool IsTrainingImage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg";

    internal static string ResolveTrainingModelPath(string installedModelPath, string workspaceModelPath) =>
        File.Exists(installedModelPath) ? installedModelPath : workspaceModelPath;

    // The training panel shows the workspace copy (workspace.ModelPath) ahead of the runtime
    // installation, so keep that copy in step with any model that was just trained or installed.
    // Failure here must not fail an install that has already committed at runtime.
    private static void RefreshWorkspaceModel(TrainingWorkspace workspace, string sourcePath)
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

    private async Task PushTrainingSampleAsync(string gameId, TrainingWorkspace workspace, TrainingSampleRecord sample,
        string messageName = "trainingSample", string? requestId = null)
    {
        var imagePath = Path.Combine(workspace.SamplesPath, sample.ImageFile);
        if (!File.Exists(imagePath))
            throw new FileNotFoundException("The training sample image is missing.", imagePath);

        _ipc.Broadcast(messageName, JsonSerializer.SerializeToElement(new
        {
            sample,
            gameId,
            requestId,
            imageData = "data:image/png;base64," + Convert.ToBase64String(await File.ReadAllBytesAsync(imagePath)),
        }, Wire.Options));
    }

    private void PushTrainingProgress(string gameId, string status, string message, int? percent = null) =>
        _ipc.Broadcast("trainingProgress", JsonSerializer.SerializeToElement(new
        {
            gameId,
            status,
            message,
            percent,
        }, Wire.Options));
}
#endif
