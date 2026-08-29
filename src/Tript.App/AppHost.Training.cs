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

    private void WithTrainingWorkspaceLock(Action action)
    {
        _trainingWorkspaceGate.Wait();
        try
        {
            action();
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
        if (!File.Exists(runtimeEvents))
            throw new FileNotFoundException($"No event definitions exist for game '{gameId}'.", runtimeEvents);

        workspace.EnsureDirectories();
        File.Copy(runtimeEvents, workspace.EventsPath, overwrite: true);
        var runtimeModel = Path.Combine(runtimeRoot, "model.onnx");
        if (File.Exists(runtimeModel))
            File.Copy(runtimeModel, workspace.ModelPath, overwrite: true);
        return workspace;
    }

    internal void PushTraining(string? requestedGameId)
    {
        WithTrainingWorkspaceLock(() => PushTrainingCore(requestedGameId));
    }

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
        var samples = new TrainingSampleStore(workspace).List();
        var dataset = new
        {
            trainingImages = CountTrainingImages(workspace.DatasetPath, "train"),
            validationImages = CountTrainingImages(workspace.DatasetPath, "val"),
        };
        OnnxModelMetadata? metadata = null;
        var modelPath = File.Exists(workspace.ModelPath)
            ? workspace.ModelPath
            : ModelService.GetModelPath(workspace.GameId);
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
                samples,
                dataset,
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

    internal void ImportTrainingAssets(ImportTrainingParameters? parameters)
    {
        WithTrainingWorkspaceLock(() => ImportTrainingAssetsCore(parameters));
    }

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

    internal void CaptureTrainingSample(CaptureTrainingSampleParameters? parameters)
    {
        WithTrainingWorkspaceLock(() => CaptureTrainingSampleCore(parameters));
    }

    private void CaptureTrainingSampleCore(CaptureTrainingSampleParameters? parameters)
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
                media.Width, media.Height, labels, File.ReadAllBytes(temporaryPath), definitions);
            PushTrainingProgress(parameters.GameId, "sampleSaved", sample.Id);
            PushTrainingSample(parameters.GameId, workspace, sample);
            PushTrainingCore(parameters.GameId);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    internal void GetTrainingSample(TrainingSampleParameters? parameters)
    {
        WithTrainingWorkspaceLock(() => GetTrainingSampleCore(parameters));
    }

    private void GetTrainingSampleCore(TrainingSampleParameters? parameters)
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

        PushTrainingSample(parameters.GameId, workspace, sample, parameters.PreviewOnly
            ? "trainingSamplePreview"
            : "trainingSample", parameters.RequestId);
    }

    internal void UpdateTrainingEvents(UpdateTrainingEventsParameters? parameters)
    {
        WithTrainingWorkspaceLock(() => UpdateTrainingEventsCore(parameters));
    }

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
        var classIdMap = orderedEvents
            .Select((eventDefinition, index) => new { Old = eventDefinition.ClassId, New = index })
            .ToDictionary(pair => pair.Old, pair => pair.New);
        TrainingEventValidator.ValidateRegions(orderedEvents);
        TrainingEventValidator.ValidateSubtractorReferences(orderedEvents);
        var sampleStore = new TrainingSampleStore(workspace);
        var originalMetadata = sampleStore.RemapClassIds(classIdMap);
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
            sampleStore.RestoreMetadata(originalMetadata);
            throw;
        }
        PushTrainingProgress(parameters.GameId, "eventsUpdated", "Training events updated.");
        PushTrainingCore(parameters.GameId);
    }

    internal void UpdateTrainingSample(UpdateTrainingSampleParameters? parameters)
    {
        WithTrainingWorkspaceLock(() => UpdateTrainingSampleCore(parameters));
    }

    private void UpdateTrainingSampleCore(UpdateTrainingSampleParameters? parameters)
    {
        if (parameters is null)
            throw new ArgumentException("Training sample parameters are required.");
        var workspace = EnsureTrainingWorkspace(parameters.GameId);
        var labels = parameters.Labels.Select(ToTrainingLabel).ToList();
        var sample = new TrainingSampleStore(workspace).UpdateLabels(parameters.SampleId, labels,
            workspace.LoadDefinitions());
        PushTrainingProgress(parameters.GameId, "sampleUpdated", sample.Id);
        PushTrainingCore(parameters.GameId);
    }

    internal void SuggestTrainingLabels(SuggestTrainingLabelsParameters? parameters)
    {
        WithTrainingWorkspaceLock(() => SuggestTrainingLabelsCore(parameters));
    }

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

        var detections = ModelPredictionService.Predict(modelPath,
            File.ReadAllBytes(imagePath), ModelService.LoadEventDefinitions(workspace.GameId));
        var suggestions = TrainingLabelSuggestionFilter.Merge(sample.Labels, detections);
        _ipc.Broadcast("trainingLabelSuggestions", JsonSerializer.SerializeToElement(new
        {
            gameId = parameters.GameId,
            sampleId = parameters.SampleId,
            requestId = parameters.RequestId,
            suggestions,
        }, Wire.Options));
    }

    internal void DeleteTrainingSample(TrainingSampleParameters? parameters)
    {
        WithTrainingWorkspaceLock(() => DeleteTrainingSampleCore(parameters));
    }

    private void DeleteTrainingSampleCore(TrainingSampleParameters? parameters)
    {
        if (parameters is null)
            throw new ArgumentException("Training sample parameters are required.");
        var workspace = EnsureTrainingWorkspace(parameters.GameId);
        new TrainingSampleStore(workspace).Delete(parameters.SampleId);
        PushTrainingProgress(parameters.GameId, "sampleDeleted", parameters.SampleId);
        PushTrainingCore(parameters.GameId);
    }

    internal void StartTraining(StartTrainingParameters? parameters)
    {
        WithTrainingWorkspaceLock(() => StartTrainingCore(parameters));
    }

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

        lock (_trainingGate)
        {
            if (_trainingCancellation is not null)
                throw new InvalidOperationException("Training is already running.");

            _trainingCancellation = new CancellationTokenSource();
            _trainingGameId = parameters.GameId;
            var cancellation = _trainingCancellation;
            _ = RunTrainingAsync(workspace, imageSize.Value, parameters, cancellation);
        }
        PushTrainingProgress(parameters.GameId, "started",
            $"Training is running in a console window. Requested device: {parameters.Device}; " +
            $"epochs: {parameters.Epochs}; input: {imageSize}x{imageSize}.");
    }

    internal void CancelTraining()
    {
        lock (_trainingGate)
        {
            _trainingRunner.Cancel();
            _trainingCancellation?.Cancel();
        }
    }

    internal void InstallTrainingModelCommand(TrainingGameParameters? parameters)
    {
        WithTrainingWorkspaceLock(() => InstallTrainingModelCommandCore(parameters));
    }

    private void InstallTrainingModelCommandCore(TrainingGameParameters? parameters)
    {
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.GameId))
            throw new ArgumentException("A game id is required to install a model.");
        var workspace = EnsureTrainingWorkspace(parameters.GameId);
        var source = Path.Combine(workspace.DatasetPath, "model.onnx");
        var result = InstallTrainingModel(parameters.GameId, source);
        PushTrainingProgress(parameters.GameId, "completed", $"Installed {result.ModelPath}.");
        PushTrainingCore(parameters.GameId);
    }

    private async Task RunTrainingAsync(TrainingWorkspace workspace, int imageSize,
        StartTrainingParameters parameters, CancellationTokenSource cancellation)
    {
        await _trainingWorkspaceGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var result = await _trainingRunner.RunAsync(workspace, imageSize, parameters.Epochs,
                parameters.Device, parameters.BaseModel,
                message => PushTrainingProgress(parameters.GameId, "progress", message),
                cancellation.Token).ConfigureAwait(false);
            var installed = InstallTrainingModel(parameters.GameId, result.DatasetModelPath);
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

    private void PushTrainingSample(string gameId, TrainingWorkspace workspace, TrainingSampleRecord sample,
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
            imageData = "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(imagePath)),
        }, Wire.Options));
    }

    private void PushTrainingProgress(string gameId, string status, string message) =>
        _ipc.Broadcast("trainingProgress", JsonSerializer.SerializeToElement(new
        {
            gameId,
            status,
            message,
        }, Wire.Options));
}
#endif
