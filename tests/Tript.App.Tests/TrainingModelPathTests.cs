// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

#if TRIPT_TRAINING

using Tript.App;
using Xunit;

namespace Tript.App.Tests;

public sealed class TrainingModelPathTests
{
    [Fact]
    public void ResolveTrainingModelPath_prefers_the_newly_installed_runtime_model()
    {
        var root = Path.Combine(Path.GetTempPath(), "tript-model-path-" + Guid.NewGuid().ToString("N"));
        var installed = Path.Combine(root, "models", "Overwatch", "model.onnx");
        var workspace = Path.Combine(root, "training", "Overwatch", "model.onnx");

        Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
        Directory.CreateDirectory(Path.GetDirectoryName(workspace)!);
        File.WriteAllBytes(installed, [1]);
        File.WriteAllBytes(workspace, [2]);

        try
        {
            Assert.Equal(installed, AppHost.ResolveTrainingModelPath(installed, workspace));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveTrainingModelPath_falls_back_to_the_workspace_model()
    {
        var root = Path.Combine(Path.GetTempPath(), "tript-model-path-" + Guid.NewGuid().ToString("N"));
        var installed = Path.Combine(root, "models", "Overwatch", "model.onnx");
        var workspace = Path.Combine(root, "training", "Overwatch", "model.onnx");

        Directory.CreateDirectory(Path.GetDirectoryName(workspace)!);
        File.WriteAllBytes(workspace, [2]);

        try
        {
            Assert.Equal(workspace, AppHost.ResolveTrainingModelPath(installed, workspace));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

#endif
