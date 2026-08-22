// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

#if TRIPT_TRAINING

using System.Diagnostics;
using System.Text.Json;
using Tript.Detection;

namespace Tript.App.Training;

internal sealed record TrainingRunResult(string DatasetModelPath, int ImageSize, string Device);

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
        if (Directory.Exists(workspace.SamplesPath)
            && Directory.EnumerateFiles(workspace.SamplesPath, "*.json").Any())
        {
            await RunProcessAsync(python, exportScript, workspace.RootPath,
                ["--size", imageSize.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                progress, cancellationToken).ConfigureAwait(false);
        }
        else if (File.Exists(Path.Combine(workspace.DatasetPath, "dataset.yaml")))
        {
            progress("USING_IMPORTED_DATASET existing dataset.yaml");
        }
        else
        {
            throw new InvalidDataException("The workspace contains neither samples nor an imported dataset.");
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
        var mismatch = ModelEventCompatibility.FindMismatch(definitions, metadata);
        if (mismatch is not null)
            throw new InvalidDataException($"The trained model and events.json do not match: {mismatch}");

        var selectedDevice = device == "auto" ? "auto (see training progress)" : device;
        progress($"VALIDATED input={metadata.InputWidth}x{metadata.InputHeight} classes={metadata.ClassCount}");
        return new TrainingRunResult(modelPath, imageSize, selectedDevice);
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

        var virtualEnvironment = Path.Combine(AppContext.BaseDirectory, ".venv", "Scripts", "python.exe");
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
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (var prefix in python.PrefixArguments)
            process.StartInfo.ArgumentList.Add(prefix);
        process.StartInfo.ArgumentList.Add("-u");
        process.StartInfo.ArgumentList.Add(script);
        process.StartInfo.ArgumentList.Add(workspace);
        foreach (var argument in scriptArguments)
            process.StartInfo.ArgumentList.Add(argument);

        process.OutputDataReceived += (_, args) => Report(progress, args.Data);
        process.ErrorDataReceived += (_, args) => Report(progress, "stderr: " + args.Data);

        if (!process.Start())
            throw new InvalidOperationException($"Could not start Python: {python.FileName}");

        lock (_gate)
            _process = process;

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            process.WaitForExit();
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
            throw new InvalidOperationException($"Training process failed with exit code {process.ExitCode}.");
    }

    private static void Report(Action<string> progress, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        try
        {
            progress(line);
        }
        catch
        {
            // A disconnected UI must not take down the training process.
        }
    }
}

internal sealed record PythonCommand(string FileName, IReadOnlyList<string> PrefixArguments);

#endif
