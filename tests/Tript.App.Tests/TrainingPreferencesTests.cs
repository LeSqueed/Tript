// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

#if TRIPT_TRAINING

using Tript.App.Training;
using Xunit;

namespace Tript.App.Tests;

public sealed class TrainingPreferencesTests
{
    [Fact]
    public void SavePreferences_round_trips_through_the_workspace_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "tript-training-prefs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var workspace = TrainingWorkspace.AtRoot("Overwatch", root);
            Assert.Null(workspace.LoadPreferences());

            workspace.SavePreferences(new TrainingPreferences
            {
                Epochs = 50,
                Device = "rocm",
                AugmentCopies = 4,
            });

            var loaded = workspace.LoadPreferences();
            Assert.NotNull(loaded);
            Assert.Equal(50, loaded!.Epochs);
            Assert.Equal("rocm", loaded.Device);
            Assert.Equal(4, loaded.AugmentCopies);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LoadPreferences_ignores_a_corrupted_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "tript-training-prefs-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var workspace = TrainingWorkspace.AtRoot("Overwatch", root);
            File.WriteAllText(workspace.PreferencesPath, "{ not json ");

            Assert.Null(workspace.LoadPreferences());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Revision_ignores_the_preferences_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "tript-training-prefs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var workspace = TrainingWorkspace.AtRoot("Overwatch", root);
            Directory.CreateDirectory(workspace.RootPath);
            File.WriteAllText(workspace.EventsPath, "[]");

            var before = workspace.Revision();
            workspace.SavePreferences(new TrainingPreferences
            {
                Epochs = 50,
                Device = "cuda",
                AugmentCopies = 2,
            });
            Assert.Equal(before, workspace.Revision());

            File.WriteAllText(workspace.EventsPath, "[1]");
            Assert.NotEqual(before, workspace.Revision());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

#endif
