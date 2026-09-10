// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

#if TRIPT_TRAINING

using System.Diagnostics;
using System.Text.Json;
using Tript.Detection;

namespace Tript.App.Training;

internal sealed class TrainingProgressUpdate
{
    public string Status { get; init; } = string.Empty;
    public int Epoch { get; init; }
    public int Epochs { get; init; }
    public double? Loss { get; init; }
    public double? Map50 { get; init; }

    internal static TrainingProgressUpdate? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<TrainingProgressUpdate>(json,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal sealed class TrainingEventCoverage
{
    public int ClassId { get; init; }
    public string Name { get; init; } = string.Empty;
    public int SampleCount { get; init; }
    public int TrainingSamples { get; init; }
    public int ValidationSamples { get; init; }
}

internal sealed class TrainingDatasetExportSummary
{
    public int Size { get; init; }
    public int Augment { get; init; }
    public int SampleCount { get; init; }
    public int CropCount { get; init; }
    public int AugmentedCrops { get; init; }
    public int InvalidLabels { get; init; }
    public int SkippedSamples { get; init; }
    public int TrainingSamples { get; init; }
    public int ValidationSamples { get; init; }
    public int TrainingCrops { get; init; }
    public int ValidationCrops { get; init; }
    public List<TrainingEventCoverage> EventCoverage { get; init; } = [];
    public List<string> Warnings { get; init; } = [];

    internal string ProgressMessage()
    {
        var coverage = string.Join(", ", EventCoverage.Select(item =>
            $"{item.Name}: {item.TrainingSamples} train/{item.ValidationSamples} validation"));
        var message = $"Dataset exported: {TrainingSamples} train and {ValidationSamples} validation " +
            $"frames ({TrainingCrops}/{ValidationCrops} crops). Event coverage: {coverage}.";
        if (Augment > 0)
            message += $" Training data augmented with {Augment} copied crops per crop " +
                $"({AugmentedCrops} extra); validation is untouched.";
        if (InvalidLabels > 0 || SkippedSamples > 0)
            message += $" Skipped {InvalidLabels} invalid labels and {SkippedSamples} samples.";
        if (Warnings.Count > 0)
            message += " Warnings: " + string.Join(" ", Warnings);
        return message;
    }
}

internal sealed class TrainingRunner
{
    private readonly object _gate = new();
    private Process? _process;

    internal static void ValidateEpochs(int epochs)
    {
        if (epochs <= 0)
            throw new ArgumentOutOfRangeException(nameof(epochs), "Epochs must be positive.");
    }

    internal static void ValidateAugmentCopies(int augmentCopies)
    {
        if (augmentCopies < 0)
            throw new ArgumentOutOfRangeException(nameof(augmentCopies),
                "Augmented copies per crop must be non-negative.");
    }

    internal async Task PrepareDatasetAsync(TrainingWorkspace workspace, int imageSize,
        int augmentCopies, Action<string, TrainingProgressUpdate?> progress,
        CancellationToken cancellationToken)
    {
        if (imageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(imageSize));
        ValidateAugmentCopies(augmentCopies);

        var (python, exportScript, _) = ResolveTrainingEnvironment();
        if (!Directory.Exists(workspace.SamplesPath)
            || !Directory.EnumerateFiles(workspace.SamplesPath, "*.json").Any())
        {
            throw new InvalidDataException("The workspace contains no editable samples.");
        }

        await RunProcessAsync(python, exportScript, workspace.RootPath,
            ["--size", imageSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
             "--augment", augmentCopies.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            message => progress(message, null), cancellationToken).ConfigureAwait(false);
        var exportSummary = LoadExportSummary(workspace)
            ?? throw new InvalidDataException("Dataset export completed without export.json.");
        progress(exportSummary.ProgressMessage(), null);
    }

    internal async Task PrepareOcrDatasetAsync(TrainingWorkspace workspace, string? objectSamplesPath,
        Action<string> progress, CancellationToken cancellationToken)
    {
        var (python, exportScript, _) = ResolveOcrTrainingEnvironment();
        var arguments = new List<string>();
        if (!string.IsNullOrWhiteSpace(objectSamplesPath) && Directory.Exists(objectSamplesPath))
            arguments.AddRange(["--object-samples", objectSamplesPath]);
        await RunProcessAsync(python, exportScript, workspace.RootPath, arguments, progress,
            cancellationToken).ConfigureAwait(false);
        if (!Directory.Exists(Path.Combine(workspace.DatasetPath, "ocr")))
            throw new InvalidDataException("OCR dataset export produced no dataset/ocr directory.");
    }

    internal async Task<string> TrainOcrModelAsync(TrainingWorkspace workspace, int epochs,
        string device, Action<string, TrainingProgressUpdate?> progress, CancellationToken cancellationToken)
    {
        ValidateEpochs(epochs);
        var (python, _, trainScript) = ResolveOcrTrainingEnvironment();
        var (paddleRoot, config, pretrained, dict) = ResolveOcrFinetuneInputs();
        if (!File.Exists(Path.Combine(paddleRoot, "tools", "train.py")))
            throw new FileNotFoundException("The PaddleOCR checkout is not installed next to the app.", paddleRoot);
        if (!File.Exists(pretrained + ".pdparams"))
            throw new FileNotFoundException("The PP-OCRv4-en fine-tune base checkpoint is not installed.",
                pretrained + ".pdparams");
        var progressPath = workspace.TrainingProgressPath;
        try { if (File.Exists(progressPath)) File.Delete(progressPath); }
        catch (IOException) { }

        var arguments = new List<string>
        {
            "--epochs", epochs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--device", device,
            "--paddle-root", paddleRoot,
            "--config", config,
            "--pretrained", pretrained,
            "--dict", dict,
        };
        using var pollerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var poller = PollTrainingProgressAsync(progressPath, progress, pollerCancellation.Token);
        try
        {
            await RunProcessAsync(python, trainScript, workspace.RootPath, arguments,
                message => progress(message, null), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            pollerCancellation.Cancel();
            await poller.ConfigureAwait(false);
        }

        var modelPath = Path.Combine(workspace.DatasetPath, "ocr_model.onnx");
        if (!File.Exists(modelPath) || !File.Exists(Path.Combine(workspace.DatasetPath, "ocr_dict.txt")))
            throw new InvalidDataException("OCR training did not produce ocr_model.onnx and ocr_dict.txt.");
        return modelPath;
    }

    internal async Task<string> TrainModelAsync(TrainingWorkspace workspace, int imageSize,
        int epochs, string device, string? baseModel, Action<string, TrainingProgressUpdate?> progress,
        CancellationToken cancellationToken)
    {
        if (imageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(imageSize));
        ValidateEpochs(epochs);

        var (python, _, trainScript) = ResolveTrainingEnvironment();

        var progressPath = workspace.TrainingProgressPath;
        try
        {
            if (File.Exists(progressPath))
                File.Delete(progressPath);
        }
        catch (IOException)
        {
        }

        var trainArguments = new List<string>
        {
            "--size", imageSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--epochs", epochs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--device", device,
        };
        if (!string.IsNullOrWhiteSpace(baseModel))
        {
            trainArguments.Add("--base-model");
            trainArguments.Add(baseModel);
        }

        using var pollerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var poller = PollTrainingProgressAsync(progressPath, progress, pollerCancellation.Token);
        try
        {
            await RunProcessAsync(python, trainScript, workspace.RootPath, trainArguments,
                message => progress(message, null), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            pollerCancellation.Cancel();
            await poller.ConfigureAwait(false);
        }

        var modelPath = Path.Combine(workspace.DatasetPath, "model.onnx");
        if (!File.Exists(modelPath))
            throw new InvalidDataException($"Training completed without producing '{modelPath}'.");
        return modelPath;
    }

    internal void ValidateTrainingResult(TrainingWorkspace workspace, string modelPath,
        Action<string> progress)
    {
        var metadata = OnnxModelInspector.Inspect(modelPath);
        var definitions = workspace.LoadDefinitions();
        var mismatch = ModelApiV1Compatibility.FindMismatch(definitions, metadata);
        if (mismatch is not null)
            throw new InvalidDataException($"The trained model and events.json do not match: {mismatch}");

        var regionGroups = workspace.LoadRegionGroups();
        ValidateKnownSamples(workspace, modelPath, definitions, regionGroups);

        progress($"VALIDATED input={metadata.InputWidth}x{metadata.InputHeight} classes={metadata.ClassCount}");
    }

    private static (PythonCommand Python, string ExportScript, string TrainScript) ResolveTrainingEnvironment()
    {
        var scriptsPath = Path.Combine(AppContext.BaseDirectory, "Training", "Scripts");
        var exportScript = Path.Combine(scriptsPath, "export_dataset.py");
        var trainScript = Path.Combine(scriptsPath, "train_model.py");
        if (!File.Exists(exportScript) || !File.Exists(trainScript))
        {
            throw new FileNotFoundException(
                "The training scripts were not included in this training-enabled build.", scriptsPath);
        }

        return (FindPython(), exportScript, trainScript);
    }

    private static (PythonCommand Python, string ExportScript, string TrainScript) ResolveOcrTrainingEnvironment()
    {
        var scriptsPath = Path.Combine(AppContext.BaseDirectory, "Training", "Scripts");
        var exportScript = Path.Combine(scriptsPath, "export_ocr_dataset.py");
        var trainScript = Path.Combine(scriptsPath, "finetune_ocr.py");
        if (!File.Exists(exportScript) || !File.Exists(trainScript))
            throw new FileNotFoundException("The OCR training scripts were not included in this build.", scriptsPath);
        return (FindOcrPython(), exportScript, trainScript);
    }

    internal static PythonCommand FindOcrPython()
    {
        var configured = Environment.GetEnvironmentVariable("TRIPT_OCR_PYTHON");
        if (!string.IsNullOrWhiteSpace(configured))
            return new PythonCommand(configured, []);
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        var venv = OperatingSystem.IsWindows()
            ? Path.Combine(root, ".venv-paddle", "Scripts", "python.exe")
            : Path.Combine(root, ".venv-paddle", "bin", "python");
        return File.Exists(venv) ? new PythonCommand(venv, []) : FindPython();
    }

    internal static (string PaddleRoot, string Config, string Pretrained, string Dict) ResolveOcrFinetuneInputs()
    {
        var appDir = AppContext.BaseDirectory;
        return (
            Path.GetFullPath(Path.Combine(appDir, "..", "paddleocr")),
            Path.Combine(appDir, "Training", "Scripts", "en_PP-OCRv4_rec_finetune.yml"),
            Path.Combine(appDir, "data", "ocr", "finetune-base", "en_PP-OCRv4_rec_train", "best_accuracy"),
            Path.Combine(appDir, "data", "ocr", "ocr_dict.txt"));
    }

    private static async Task PollTrainingProgressAsync(string progressPath,
        Action<string, TrainingProgressUpdate?> progress, CancellationToken cancellationToken)
    {
        var last = string.Empty;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                if (!File.Exists(progressPath))
                    continue;
                string current;
                try
                {
                    current = File.ReadAllText(progressPath);
                }
                catch (IOException)
                {
                    continue;
                }
                if (current == last)
                    continue;
                last = current;

                var update = TrainingProgressUpdate.Parse(current);
                if (update is null)
                    continue;

                switch (update.Status)
                {
                    case "starting":
                        progress("Loading the base model and starting training…", update);
                        break;
                    case "training":
                        var parts = new List<string> { $"Epoch {update.Epoch}/{update.Epochs}" };
                        if (update.Loss is not null)
                            parts.Add($"loss {update.Loss:0.0000}");
                        if (update.Map50 is not null)
                            parts.Add($"mAP50 {update.Map50:0.0000}");
                        progress(string.Join(" · ", parts), update);
                        break;
                    case "exporting":
                        progress("Exporting the trained model to ONNX…", update);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal static TrainingDatasetExportSummary? LoadExportSummary(TrainingWorkspace workspace)
    {
        var path = Path.Combine(workspace.DatasetPath, "export.json");
        if (!File.Exists(path))
            return null;
        return JsonSerializer.Deserialize<TrainingDatasetExportSummary>(
            File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });
    }

    private static void ValidateKnownSamples(TrainingWorkspace workspace, string modelPath,
        IReadOnlyList<EventDefinition> definitions,
        IReadOnlyList<TrainingRegionGroup> regionGroups)
    {
        var samples = new TrainingSampleStore(workspace).List();
        var effectiveDefinitions = TrainingRegionResolver.MaterializeEffectiveRegions(definitions,
            regionGroups);
        var required = definitions
            .Where(definition => definition.DetectionKind == DetectionKind.Object)
            .Where(definition => definition.BookmarkType is not null)
            .Append(definitions.Where(definition => definition.DetectionKind == DetectionKind.Object)
                .MaxBy(definition => definition.ClassId)!)
            .Where(definition => definition is not null)
            .DistinctBy(definition => definition.ClassId);

        foreach (var definition in required)
        {
            var candidates = samples
                .Where(sample => TrainingLabelValidator.FindError(sample.Labels, definitions,
                        requireLabel: true, regionGroups: regionGroups) is null
                    && sample.Labels.Any(label => label.ClassId == definition.ClassId))
                .Take(3)
                .ToList();
            if (candidates.Count == 0)
                throw new InvalidDataException(
                    $"No labeled sample exists to validate '{definition.Name}' (class {definition.ClassId}).");

            var detected = candidates.Any(sample => ModelPredictionService.Predict(modelPath,
                    File.ReadAllBytes(Path.Combine(workspace.SamplesPath, sample.ImageFile)),
                    effectiveDefinitions)
                .Any(result => result.ClassId == definition.ClassId));
            if (!detected)
            {
                throw new InvalidDataException(
                    $"The trained model failed the sample check for '{definition.Name}' " +
                    $"(class {definition.ClassId}); it was not installed.");
            }
        }
    }

    internal void Cancel()
    {
        lock (_gate)
        {
            try
            {
                if (_process is { HasExited: false })
                    _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    internal static PythonCommand FindPython()
    {
        var configured = Environment.GetEnvironmentVariable("TRIPT_PYTHON");
        if (!string.IsNullOrWhiteSpace(configured))
            return new PythonCommand(configured, []);

        var virtualEnvironment = OperatingSystem.IsWindows()
            ? Path.Combine(AppContext.BaseDirectory, ".venv", "Scripts", "python.exe")
            : Path.Combine(AppContext.BaseDirectory, ".venv", "bin", "python");
        if (File.Exists(virtualEnvironment))
            return new PythonCommand(virtualEnvironment, []);

        return new PythonCommand("python", []);
    }

    private async Task RunProcessAsync(PythonCommand python, string script, string workspace,
        IReadOnlyList<string> scriptArguments, Action<string> progress,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = python.FileName,
                WorkingDirectory = workspace,

                UseShellExecute = true,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal,
            },
        };
        foreach (var prefix in python.PrefixArguments)
            process.StartInfo.ArgumentList.Add(prefix);
        process.StartInfo.ArgumentList.Add("-u");
        process.StartInfo.ArgumentList.Add(script);
        process.StartInfo.ArgumentList.Add(workspace);
        foreach (var argument in scriptArguments)
            process.StartInfo.ArgumentList.Add(argument);

        if (!process.Start())
            throw new InvalidOperationException($"Could not start Python: {python.FileName}");

        lock (_gate)
            _process = process;

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Cancel();
            throw;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_process, process))
                    _process = null;
            }
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Training process '{Path.GetFileName(script)}' failed with exit code {process.ExitCode}. " +
                "See the training console window for details.");
    }
}

internal sealed record PythonCommand(string FileName, IReadOnlyList<string> PrefixArguments);

#endif
