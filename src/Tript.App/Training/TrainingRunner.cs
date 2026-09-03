// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

#if TRIPT_TRAINING

using System.Diagnostics;
using System.Text.Json;
using Tript.Detection;

namespace Tript.App.Training;

internal sealed record TrainingRunResult(string DatasetModelPath, int ImageSize, string Device);

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
    public int SampleCount { get; init; }
    public int CropCount { get; init; }
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
        if (Warnings.Count > 0)
            message += " Warnings: " + string.Join(" ", Warnings);
        return message;
    }
}

internal sealed class TrainingRunner
{
    private readonly object _gate = new();
    private Process? _process;

    internal async Task<TrainingRunResult> RunAsync(TrainingWorkspace workspace, int imageSize,
        int epochs, string device, string? baseModel, Action<string> progress,
        CancellationToken cancellationToken)
    {
        if (imageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(imageSize));
        if (epochs <= 0)
            throw new ArgumentOutOfRangeException(nameof(epochs));

        var scriptsPath = Path.Combine(AppContext.BaseDirectory, "Training", "Scripts");
        var exportScript = Path.Combine(scriptsPath, "export_dataset.py");
        var trainScript = Path.Combine(scriptsPath, "train_model.py");
        if (!File.Exists(exportScript) || !File.Exists(trainScript))
        {
            throw new FileNotFoundException(
                "The training scripts were not included in this training-enabled build.", scriptsPath);
        }

        var python = FindPython();
        if (!Directory.Exists(workspace.SamplesPath)
            || !Directory.EnumerateFiles(workspace.SamplesPath, "*.json").Any())
        {
            throw new InvalidDataException("The workspace contains no editable samples.");
        }

        await RunProcessAsync(python, exportScript, workspace.RootPath,
            ["--size", imageSize.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            progress, cancellationToken).ConfigureAwait(false);
        var exportSummary = LoadExportSummary(workspace)
            ?? throw new InvalidDataException("Dataset export completed without export.json.");
        progress(exportSummary.ProgressMessage());

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

        await RunProcessAsync(python, trainScript, workspace.RootPath, trainArguments,
            progress, cancellationToken).ConfigureAwait(false);

        var modelPath = Path.Combine(workspace.DatasetPath, "model.onnx");
        if (!File.Exists(modelPath))
            throw new InvalidDataException($"Training completed without producing '{modelPath}'.");

        var metadata = OnnxModelInspector.Inspect(modelPath);
        var definitions = JsonSerializer.Deserialize<List<EventDefinition>>(
            File.ReadAllText(workspace.EventsPath), new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }) ?? [];
        var mismatch = ModelApiV1Compatibility.FindMismatch(definitions, metadata);
        if (mismatch is not null)
            throw new InvalidDataException($"The trained model and events.json do not match: {mismatch}");

        ValidateKnownSamples(workspace, modelPath, definitions);

        var selectedDevice = device == "auto" ? "auto (see training progress)" : device;
        progress($"VALIDATED input={metadata.InputWidth}x{metadata.InputHeight} classes={metadata.ClassCount}");
        return new TrainingRunResult(modelPath, imageSize, selectedDevice);
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
        IReadOnlyList<EventDefinition> definitions)
    {
        var samples = new TrainingSampleStore(workspace).List();
        var required = definitions
            .Where(definition => definition.BookmarkType is not null)
            .Append(definitions.MaxBy(definition => definition.ClassId)!)
            .DistinctBy(definition => definition.ClassId);

        foreach (var definition in required)
        {
            var candidates = samples
                .Where(sample => sample.Labels.Any(label => label.ClassId == definition.ClassId))
                .Take(3)
                .ToList();
            if (candidates.Count == 0)
                throw new InvalidDataException(
                    $"No labeled sample exists to validate '{definition.Name}' (class {definition.ClassId}).");

            var detected = candidates.Any(sample => ModelPredictionService.Predict(modelPath,
                    File.ReadAllBytes(Path.Combine(workspace.SamplesPath, sample.ImageFile)), definitions)
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
                // The process exited between the check and Kill.
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
                // Training is intentionally visible: Ultralytics owns the detailed progress display,
                // while the web UI reports the high-level running state.
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
