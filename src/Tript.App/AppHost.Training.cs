// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

#if TRIPT_TRAINING
using System.Text.Json;
using Serilog;
using Tript.App.Content;
using Tript.App.Ipc;
using Tript.App.Training;
using Tript.App.Resolver;
using Tript.Detection;
using Tript.Media;

namespace Tript.App;

internal sealed partial class AppHost
{
    internal async Task PublishTrainingModel(PublishTrainingModelParameters? parameters, ClientHandle client)
    {
        var requestId = parameters?.RequestId?.Trim() ?? string.Empty;
        try
        {
            if (parameters is null || requestId.Length == 0 || string.IsNullOrWhiteSpace(parameters.GameId)
                || string.IsNullOrWhiteSpace(parameters.Username) || string.IsNullOrEmpty(parameters.Password))
                throw new InvalidOperationException("Enter the resolver admin username and password.");
            if (_resolverClient is null)
                throw new InvalidOperationException("No resolver URL is configured.");
            var installed = TrainingWorkspace.ForGame(parameters.GameId, TrainingPaths.InstalledModelsPath);
            using var admin = new ResolverAdminClient(_resolverClient.Config);
            var revision = await admin.PublishAsync(parameters.GameId, installed.RootPath, installed.EventsPath,
                parameters.Username.Trim(), parameters.Password, _discoveryCancellation.Token).ConfigureAwait(false);
            client.Push("trainingPublishResult", JsonSerializer.SerializeToElement(new
            {
                requestId,
                success = true,
                revision,
            }, Wire.Options));
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException
            or IOException or JsonException or TaskCanceledException)
        {
            client.Push("trainingPublishResult", JsonSerializer.SerializeToElement(new
            {
                requestId,
                success = false,
                error = exception.Message,
            }, Wire.Options));
        }
    }

    private readonly TrainingSession _training = new();
    private readonly SemaphoreSlim _trainingWorkspaceGate = new(1, 1);

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

    internal Task PushTraining(string? requestedGameId) => WithTrainingWorkspaceLockAsync(() =>
    {
        PushTrainingCore(requestedGameId);
        return Task.CompletedTask;
    });

    private void PushTrainingCore(string? requestedGameId, string? requestId = null,
        string? updateKind = null)
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

        var workspace = TrainingWorkspaceEditor.EnsureWorkspace(gameId);
        var definitions = workspace.LoadDefinitions();
        var regionGroups = workspace.LoadRegionGroups();
        var samples = new TrainingSampleStore(workspace).List();
        var invalidSamples = TrainingWorkspaceEditor.FindInvalidSamples(samples, definitions, regionGroups);
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
            trainingImages = TrainingWorkspaceEditor.CountImages(workspace.DatasetPath, "train"),
            validationImages = TrainingWorkspaceEditor.CountImages(workspace.DatasetPath, "val"),
            eventCoverage = exportSummary?.EventCoverage ?? [],
            warnings = exportSummary?.Warnings ?? [],
        };
        OnnxModelMetadata? metadata = null;

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

        var (trainingActive, trainingPhase) = _training.StateFor(gameId);

        _ipc.Broadcast("training", JsonSerializer.SerializeToElement(new
        {
            requestId,
            updateKind,
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
                trainingActive,
                trainingPhase,
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

        _training.ThrowIfRunning("Stop training before importing a workspace.");

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

    internal async Task CaptureTrainingSample(CaptureTrainingSampleParameters? parameters)
        => await WithTrainingWorkspaceLockAsync(() => CaptureTrainingSampleCore(parameters));

    private async Task CaptureTrainingSampleCore(CaptureTrainingSampleParameters? parameters)
    {
        if (parameters is null)
            throw new ArgumentException("Training sample parameters are required.");
        var workspace = TrainingWorkspaceEditor.EnsureWorkspace(parameters.GameId);
        var sourcePath = ContentServer.ResolveWithinRoot(EffectiveRoot, parameters.FilePath)
            ?? throw new InvalidDataException("The sample source is not inside the recording folder.");
        var tools = _libraryTools.Value
            ?? throw new InvalidOperationException("FFmpeg and ffprobe are required to capture training samples.");
        var media = new MediaProbe(tools.Ffprobe).Probe(sourcePath);
        var definitions = workspace.LoadDefinitions();
        var regionGroups = workspace.LoadRegionGroups();
        var labels = parameters.Labels.Select(TrainingWorkspaceEditor.ToLabel).ToList();
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

    internal Task GetTrainingSample(TrainingSampleParameters? parameters) => GetTrainingSampleCore(parameters);

    private async Task GetTrainingSampleCore(TrainingSampleParameters? parameters)
    {
        if (parameters is null)
            throw new ArgumentException("Training sample parameters are required.");
        var workspace = TrainingWorkspace.ForGame(parameters.GameId);
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
            try
            {
                UpdateTrainingEventsCore(parameters);
                PushTrainingUpdateResult("trainingEventsUpdateResult", parameters?.RequestId, true, null);
            }
            catch (Exception exception)
            {
                PushTrainingUpdateResult("trainingEventsUpdateResult", parameters?.RequestId, false,
                    exception.Message);
            }
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
        var workspace = TrainingWorkspaceEditor.EnsureWorkspace(parameters.GameId);
        var removedLabelCount = TrainingWorkspaceEditor.ReplaceEvents(workspace, parameters.Events.ToList(),
            percent => PushTrainingProgress(parameters.GameId, "eventDeleteProgress",
                $"Removing labels from training samples… {percent}%", percent,
                requestId: parameters.RequestId));
        var removedMessage = removedLabelCount > 0
            ? $"Training events updated. Removed {removedLabelCount} label"
                + $"{(removedLabelCount == 1 ? string.Empty : "s")}."
            : "Training events updated.";
        PushTrainingProgress(parameters.GameId, "eventsUpdated", removedMessage,
            requestId: parameters.RequestId);
        PushTrainingCore(parameters.GameId, parameters.RequestId, "events");
    }

    internal async Task UpdateTrainingRegionGroups(UpdateTrainingRegionGroupsParameters? parameters)
        => await WithTrainingWorkspaceLockAsync(() =>
        {
            try
            {
                UpdateTrainingRegionGroupsCore(parameters);
                PushTrainingUpdateResult("trainingRegionGroupsUpdateResult", parameters?.RequestId, true, null);
            }
            catch (Exception exception)
            {
                PushTrainingUpdateResult("trainingRegionGroupsUpdateResult", parameters?.RequestId, false,
                    exception.Message);
            }
            return Task.CompletedTask;
        });

    private void UpdateTrainingRegionGroupsCore(UpdateTrainingRegionGroupsParameters? parameters)
    {
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.GameId))
            throw new ArgumentException("A game id is required to update training region groups.");

        TrainingEventValidator.ValidateRegionGroups(parameters.RegionGroups);
        var workspace = TrainingWorkspaceEditor.EnsureWorkspace(parameters.GameId);
        TrainingWorkspaceEditor.ReplaceRegionGroups(workspace, parameters.RegionGroups);
        PushTrainingProgress(parameters.GameId, "regionGroupsUpdated", "Training region groups updated.",
            requestId: parameters.RequestId);
        PushTrainingCore(parameters.GameId, parameters.RequestId, "regionGroups");
    }

    internal async Task UpdateTrainingSample(UpdateTrainingSampleParameters? parameters)
        => await WithTrainingWorkspaceLockAsync(() =>
        {
            try
            {
                UpdateTrainingSampleCore(parameters);
                PushTrainingUpdateResult("trainingSampleUpdateResult", parameters?.RequestId, true, null);
            }
            catch (Exception exception)
            {
                PushTrainingUpdateResult("trainingSampleUpdateResult", parameters?.RequestId, false,
                    exception.Message);
            }
            return Task.CompletedTask;
        });

    private void UpdateTrainingSampleCore(UpdateTrainingSampleParameters? parameters)
    {
        if (parameters is null)
            throw new ArgumentException("Training sample parameters are required.");
        var workspace = TrainingWorkspaceEditor.EnsureWorkspace(parameters.GameId);
        var labels = parameters.Labels.Select(TrainingWorkspaceEditor.ToLabel).ToList();
        var ocrRegions = parameters.OcrRegions.Select(TrainingWorkspaceEditor.ToOcrRegion).ToList();
        var sample = new TrainingSampleStore(workspace).UpdateLabels(parameters.SampleId, labels,
            workspace.LoadDefinitions(), workspace.LoadRegionGroups(), ocrRegions);
        PushTrainingProgress(parameters.GameId, "sampleUpdated", sample.Id,
            requestId: parameters.RequestId);
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

        var workspace = TrainingWorkspaceEditor.EnsureWorkspace(parameters.GameId);
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
        var workspace = TrainingWorkspaceEditor.EnsureWorkspace(parameters.GameId);
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

        var augmentCopies = parameters.AugmentCopies ?? 0;
        var ocrEpochs = parameters.OcrEpochs ?? parameters.Epochs;
        TrainingRunner.ValidateEpochs(parameters.Epochs);
        TrainingRunner.ValidateEpochs(ocrEpochs);
        TrainingRunner.ValidateAugmentCopies(augmentCopies);
        var workspace = TrainingWorkspaceEditor.EnsureWorkspace(parameters.GameId);
        var imageSize = parameters.ImageSize;
        var currentModelPath = TrainingWorkspaceEditor.ResolveModelPath(
            ModelService.GetModelPath(workspace.GameId), workspace.ModelPath);
        if (imageSize is null && File.Exists(currentModelPath))
            imageSize = OnnxModelInspector.Inspect(currentModelPath).InputWidth;
        imageSize ??= 640;
        var cancellation = _training.Begin(parameters.GameId, "exporting", () =>
            workspace.SavePreferences(new TrainingPreferences
            {
                Epochs = parameters.Epochs,
                Device = parameters.Device,
                AugmentCopies = augmentCopies,
                OcrEpochs = ocrEpochs,
            }));
        _ = RunTrainingAsync(workspace, imageSize.Value, augmentCopies, parameters, cancellation);
        PushTrainingProgress(parameters.GameId, "exporting",
            $"Preparing the training data. Requested device: {parameters.Device}; epochs: {parameters.Epochs}.");
    }

    internal void CancelTraining() => _training.Cancel();

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
        _training.ThrowIfRunning("Stop training before installing a model.");
        var workspace = TrainingWorkspaceEditor.EnsureWorkspace(parameters.GameId);
        var definitions = workspace.LoadDefinitions();
        var hasObjectEvents = definitions.Any(d => d.DetectionKind == DetectionKind.Object);
        var hasOcrEvents = definitions.Any(d => d.DetectionKind == DetectionKind.Ocr);
        string? modelSource = hasObjectEvents ? Path.Combine(workspace.DatasetPath, "model.onnx") : null;
        var datasetOcrModel = Path.Combine(workspace.DatasetPath, "ocr_model.onnx");
        var datasetOcrDict = Path.Combine(workspace.DatasetPath, "ocr_dict.txt");
        var haveDatasetOcr = hasOcrEvents && File.Exists(datasetOcrModel) && File.Exists(datasetOcrDict);
        if (haveDatasetOcr)
            InstallTrainingModel(parameters.GameId, modelSource, datasetOcrModel, datasetOcrDict);
        else
            InstallTrainingModel(parameters.GameId, modelSource);
        if (modelSource is not null)
            TrainingWorkspaceEditor.RefreshWorkspaceModel(workspace, modelSource);
        PushTrainingProgress(parameters.GameId, "completed",
            TrainingWorkspaceEditor.DescribeOutcome(modelSource is not null, haveDatasetOcr, haveDatasetOcr));
        PushTrainingCore(parameters.GameId);
    }

    private async Task RunTrainingAsync(TrainingWorkspace workspace, int imageSize, int augmentCopies,
        StartTrainingParameters parameters, CancellationTokenSource cancellation)
    {
        try
        {
            string sourceRevision;
            bool hasObjectEvents;
            bool hasOcrEvents;
            var scope = string.IsNullOrWhiteSpace(parameters.Scope)
                ? "all" : parameters.Scope.Trim().ToLowerInvariant();
            bool trainObject;
            bool trainOcr;
            await _trainingWorkspaceGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var definitions = workspace.LoadDefinitions();
                hasObjectEvents = definitions.Any(definition =>
                    definition.DetectionKind == DetectionKind.Object);
                hasOcrEvents = definitions.Any(definition =>
                    definition.DetectionKind == DetectionKind.Ocr);
                trainObject = hasObjectEvents && scope is "all" or "object";
                trainOcr = hasOcrEvents && scope is "all" or "ocr";
                if (!trainObject && !trainOcr)
                    throw new InvalidOperationException(
                        "The requested training scope has no matching events to train.");
                var datasetKinds = (trainObject, trainOcr) switch
                {
                    (true, true) => "object-detection dataset and OCR training data",
                    (true, false) => "object-detection dataset",
                    _ => "OCR training data",
                };
                var augmentNote = trainObject && augmentCopies > 0
                    ? $" Augmentation: {augmentCopies} extra copies per crop."
                    : string.Empty;
                PushTrainingProgress(parameters.GameId, "exporting",
                    $"Preparing the {datasetKinds}. The workspace is locked until this finishes; "
                    + "details are in the console window." + augmentNote);
                if (trainObject)
                {
                    await _training.Runner.PrepareDatasetAsync(workspace, imageSize, augmentCopies,
                        (message, _) => PushTrainingProgress(parameters.GameId, "progress", message),
                        cancellation.Token).ConfigureAwait(false);
                }
                if (trainOcr)
                {
                    await _training.Runner.PrepareOcrDatasetAsync(workspace,
                        Environment.GetEnvironmentVariable("TRIPT_OCR_OBJECT_SAMPLES"),
                        message => PushTrainingProgress(parameters.GameId, "progress", message),
                        cancellation.Token).ConfigureAwait(false);
                }
                sourceRevision = workspace.SourceRevision();
            }
            finally
            {
                _trainingWorkspaceGate.Release();
            }

            _training.SetPhase("training");
            PushTrainingCore(parameters.GameId);
            string? modelPath = null;
            string? ocrModelPath = null;
            string? ocrDictionaryPath = null;
            string? ocrDetectorPath = null;
            var ocrFineTuned = false;
            if (trainObject)
            {
                modelPath = await _training.Runner.TrainModelAsync(workspace, imageSize, parameters.Epochs,
                    parameters.Device, parameters.BaseModel,
                    (message, details) => PushTrainingProgress(parameters.GameId, "progress", message,
                        details is null || details.Epochs == 0 ? null
                            : (int)Math.Round(100.0 * details.Epoch / details.Epochs),
                        details),
                    cancellation.Token).ConfigureAwait(false);
            }
            if (trainOcr)
            {
                ocrDetectorPath = ModelService.GetOcrDetectorPath(workspace.GameId);
                if (!File.Exists(ocrDetectorPath)) ocrDetectorPath = null;
                try
                {
                    ocrModelPath = await _training.Runner.TrainOcrModelAsync(workspace,
                        parameters.OcrEpochs ?? parameters.Epochs, "cpu",
                        (message, details) => PushTrainingProgress(parameters.GameId, "progress", message,
                            details is null || details.Epochs == 0 ? null
                                : (int)Math.Round(100.0 * details.Epoch / details.Epochs),
                            details),
                        cancellation.Token).ConfigureAwait(false);
                    ocrDictionaryPath = Path.Combine(workspace.DatasetPath, "ocr_dict.txt");
                    ocrFineTuned = true;
                    PushTrainingProgress(parameters.GameId, "progress", "Fine-tuned the OCR recogniser.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warning(ex, "OCR fine-tune failed for {GameId}; using the pretrained bundle",
                        parameters.GameId);
                    ocrModelPath = ModelService.GetOcrModelPath(workspace.GameId);
                    ocrDictionaryPath = ModelService.GetOcrDictionaryPath(workspace.GameId);
                    if (!File.Exists(ocrModelPath) || !File.Exists(ocrDictionaryPath))
                        throw new FileNotFoundException(
                            "OCR fine-tune failed and no pretrained OCR bundle is installed.", ocrModelPath);
                    PushTrainingProgress(parameters.GameId, "progress",
                        "OCR fine-tune unavailable; installed the pretrained model.");
                }
            }

            await _trainingWorkspaceGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!string.Equals(sourceRevision, workspace.SourceRevision(), StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The training workspace changed after the dataset was prepared. Start training again to use the latest data.");
                }
                if (modelPath is not null)
                {
                    _training.Runner.ValidateTrainingResult(workspace, modelPath,
                        message => PushTrainingProgress(parameters.GameId, "progress", message));
                }
                var installed = InstallTrainingModel(parameters.GameId, modelPath,
                    ocrModelPath, ocrDictionaryPath, ocrDetectorPath);
                if (modelPath is not null) TrainingWorkspaceEditor.RefreshWorkspaceModel(workspace, modelPath);
                PushTrainingProgress(parameters.GameId, "completed",
                    TrainingWorkspaceEditor.DescribeOutcome(trainObject, trainOcr, ocrFineTuned));
                PushTrainingCore(parameters.GameId);
            }
            finally
            {
                _trainingWorkspaceGate.Release();
            }
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
            _training.End(cancellation);
            try
            {
                await _trainingWorkspaceGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    PushTrainingCore(parameters.GameId);
                }
                finally
                {
                    _trainingWorkspaceGate.Release();
                }
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "Training: could not refresh workspace state after training ended");
            }
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

    private void PushTrainingProgress(string gameId, string status, string message, int? percent = null,
        TrainingProgressUpdate? details = null, string? requestId = null) =>
        _ipc.Broadcast("trainingProgress", JsonSerializer.SerializeToElement(new
        {
            gameId,
            status,
            message,
            percent,
            requestId,
            details = details is null ? null : new
            {
                epoch = details.Epoch,
                epochs = details.Epochs,
                loss = details.Loss,
                map50 = details.Map50,
            },
        }, Wire.Options));

    private void PushTrainingUpdateResult(string messageName, string? requestId, bool success,
        string? error)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            return;

        _ipc.Broadcast(messageName, JsonSerializer.SerializeToElement(new
        {
            requestId,
            success,
            error,
        }, Wire.Options));
    }
}
#endif
