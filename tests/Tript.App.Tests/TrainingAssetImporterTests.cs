// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

#if TRIPT_TRAINING

using System.Text;
using Tript.App.Training;
using Xunit;

namespace Tript.App.Tests;

public sealed class TrainingAssetImporterTests
{
    [Fact]
    public void Import_requires_full_frame_samples_before_changing_workspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "tript-training-import-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var dataset = Path.Combine(source, "dataset");
        var trainImages = Path.Combine(dataset, "images", "train");
        var trainLabels = Path.Combine(dataset, "labels", "train");
        Directory.CreateDirectory(trainImages);
        Directory.CreateDirectory(trainLabels);
        File.WriteAllText(Path.Combine(source, "events.json"), """
            [{
              "id": 1,
              "name": "An event",
              "type": "Trigger",
              "classId": 0
            }]
            """, Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(trainImages, "000001.png"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(trainLabels, "000001.txt"), "0 0.5 0.5 0.25 0.25");
        File.WriteAllText(Path.Combine(trainLabels, "train.cache"), "stale cache");

        try
        {
            Assert.Throws<InvalidDataException>(() => TrainingAssetImporter.Import(
                Path.Combine(root, "source"), "Example Game", Path.Combine(root, "workspaces")));
            Assert.False(Directory.Exists(Path.Combine(root, "workspaces", "Example Game")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Import_imports_full_frame_samples_and_ignores_prepared_dataset()
    {
        var root = Path.Combine(Path.GetTempPath(), "tript-training-import-full-frame-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var samples = Path.Combine(source, "samples");
        var trainImages = Path.Combine(source, "dataset", "images", "train");
        Directory.CreateDirectory(samples);
        Directory.CreateDirectory(trainImages);
        File.WriteAllText(Path.Combine(source, "events.json"), "[{\"id\":42,\"name\":\"Imported\",\"type\":\"Trigger\",\"classId\":0}]", Encoding.UTF8);
        WriteSample(samples, "42_639224879310211120", "42 0.5 0.95 0.8 0.2");
        File.WriteAllBytes(Path.Combine(trainImages, "prepared.png"), [1, 2, 3]);

        try
        {
            var result = TrainingAssetImporter.Import(source, "Example Game", Path.Combine(root, "workspaces"));
            var workspace = TrainingWorkspace.ForGame("Example Game", Path.Combine(root, "workspaces"));
            var imported = new TrainingSampleStore(workspace).List();

            Assert.Equal("Imported", workspace.LoadDefinitions()[0].Name);
            Assert.Equal(1, result.SampleCount);
            Assert.Equal(0, result.TrainingImageCount);
            Assert.Equal(0, result.ValidationImageCount);
            Assert.Single(imported);
            Assert.Equal(2000, imported[0].ImageWidth);
            Assert.Equal(1125, imported[0].ImageHeight);
            Assert.Equal(0, imported[0].Labels[0].ClassId);
            Assert.False(File.Exists(Path.Combine(result.WorkspacePath, "dataset", "images", "train", "prepared.png")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Import_merges_into_an_existing_workspace_and_preserves_full_frame_samples()
    {
        var root = Path.Combine(Path.GetTempPath(), "tript-training-import-samples-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var samples = Path.Combine(source, "samples");
        Directory.CreateDirectory(samples);
        File.WriteAllText(Path.Combine(source, "events.json"), "[{\"id\":1,\"name\":\"Imported\",\"type\":\"Trigger\",\"classId\":0}]", Encoding.UTF8);
        WriteSample(samples, "1_639224879310211120", "1 0.5 0.5 0.25 0.25", 640, 480);
        var workspaces = Path.Combine(root, "workspaces");
        var workspace = TrainingWorkspace.ForGame("Example Game", workspaces);
        Directory.CreateDirectory(workspace.RootPath);
        File.WriteAllText(Path.Combine(workspace.RootPath, "keep.txt"), "keep me", Encoding.UTF8);

        try
        {
            var result = TrainingAssetImporter.Import(source, "Example Game", workspaces);
            var imported = new TrainingSampleStore(workspace).List();

            Assert.Single(imported);
            Assert.Equal(640, imported[0].ImageWidth);
            Assert.Equal(480, imported[0].ImageHeight);
            Assert.Equal(0, imported[0].Labels[0].ClassId);
            Assert.Equal("keep me", File.ReadAllText(Path.Combine(result.WorkspacePath, "keep.txt")));
            var store = new TrainingSampleStore(workspace);
            store.UpdateLabels(imported[0].Id, [new TrainingLabel
            {
                ClassId = 0,
                CenterX = 0.25,
                CenterY = 0.25,
                Width = 0.2,
                Height = 0.2,
            }], TrainingWorkspace.ForGame("Example Game", Path.Combine(root, "workspaces")).LoadDefinitions());
            Assert.Equal(0.25, store.LoadById(imported[0].Id).Labels[0].CenterX);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] MinimalPng(int width, int height) =>
    [
        137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82,
        (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
        (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height,
    ];

    private static void WriteSample(string directory, string stem, string label, int width = 2000, int height = 1125)
    {
        File.WriteAllBytes(Path.Combine(directory, stem + ".png"), MinimalPng(width, height));
        File.WriteAllText(Path.Combine(directory, stem + ".txt"), label, Encoding.UTF8);
    }
}

#endif
