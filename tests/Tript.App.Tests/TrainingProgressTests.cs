// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

#if TRIPT_TRAINING

using Tript.App.Training;
using Xunit;

namespace Tript.App.Tests;

public sealed class TrainingProgressTests
{
    [Fact]
    public void Parse_reads_the_training_script_heartbeat()
    {
        var update = TrainingProgressUpdate.Parse(
            """{"status":"training","epoch":12,"epochs":100,"loss":0.8321,"map50":0.4231}""");

        Assert.NotNull(update);
        Assert.Equal("training", update!.Status);
        Assert.Equal(12, update.Epoch);
        Assert.Equal(100, update.Epochs);
        Assert.Equal(0.8321, update.Loss);
        Assert.Equal(0.4231, update.Map50);
    }

    [Fact]
    public void Parse_returns_null_for_invalid_json()
    {
        Assert.Null(TrainingProgressUpdate.Parse("{ not json "));
        Assert.Null(TrainingProgressUpdate.Parse(string.Empty));
    }

    [Fact]
    public void Revision_ignores_the_training_progress_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "tript-training-progress-" + Guid.NewGuid().ToString("N"));
        try
        {
            var workspace = TrainingWorkspace.AtRoot("Overwatch", root);
            Directory.CreateDirectory(workspace.DatasetPath);
            File.WriteAllText(Path.Combine(workspace.DatasetPath, "export.json"), "{}");

            var before = workspace.Revision();
            File.WriteAllText(workspace.TrainingProgressPath,
                """{"status":"training","epoch":1,"epochs":10,"loss":1.0,"map50":null}""");
            Assert.Equal(before, workspace.Revision());

            File.WriteAllText(Path.Combine(workspace.DatasetPath, "export.json"), "{} changed");
            Assert.NotEqual(before, workspace.Revision());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

#endif
