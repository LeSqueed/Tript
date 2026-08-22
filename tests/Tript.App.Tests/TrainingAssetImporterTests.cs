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
    public void Import_copies_portable_dataset_assets_without_ultralytics_caches()
    {
        var root = Path.Combine(Path.GetTempPath(), "tript-training-import-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source", "external-id");
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
            var result = TrainingAssetImporter.Import(Path.Combine(root, "source"), "Example Game",
                Path.Combine(root, "workspaces"));

            Assert.Equal("Example Game", result.GameId);
            Assert.False(result.ModelImported);
            Assert.Equal(1, result.EventCount);
            Assert.Equal(1, result.TrainingImageCount);
            Assert.Equal(0, result.ValidationImageCount);
            Assert.True(File.Exists(Path.Combine(result.WorkspacePath, "events.json")));
            Assert.True(File.Exists(Path.Combine(result.WorkspacePath, "dataset", "images", "train", "000001.png")));
            Assert.True(File.Exists(Path.Combine(result.WorkspacePath, "dataset", "labels", "train", "000001.txt")));
            Assert.False(File.Exists(Path.Combine(result.WorkspacePath, "dataset", "labels", "train", "train.cache")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Import_merges_into_an_existing_workspace_and_preserves_samples()
    {
        var root = Path.Combine(Path.GetTempPath(), "tript-training-import-existing-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var workspaces = Path.Combine(root, "workspaces");
        var workspace = TrainingWorkspace.ForGame("Example Game", workspaces);
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(workspace.RootPath);
        File.WriteAllText(Path.Combine(source, "events.json"), "[{\"id\":1,\"name\":\"Imported\",\"type\":\"Trigger\",\"classId\":0}]", Encoding.UTF8);
        File.WriteAllText(Path.Combine(workspace.RootPath, "keep.txt"), "keep me", Encoding.UTF8);

        try
        {
            var result = TrainingAssetImporter.Import(source, "Example Game", workspaces);

            Assert.Equal("Imported", workspace.LoadDefinitions()[0].Name);
            Assert.Equal("keep me", File.ReadAllText(Path.Combine(result.WorkspacePath, "keep.txt")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Import_creates_editable_samples_from_png_dataset_pairs()
    {
        var root = Path.Combine(Path.GetTempPath(), "tript-training-import-samples-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var trainImages = Path.Combine(source, "dataset", "images", "train");
        var trainLabels = Path.Combine(source, "dataset", "labels", "train");
        Directory.CreateDirectory(trainImages);
        Directory.CreateDirectory(trainLabels);
        File.WriteAllText(Path.Combine(source, "events.json"), "[{\"id\":1,\"name\":\"Imported\",\"type\":\"Trigger\",\"classId\":0}]", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(trainImages, "000001.png"), MinimalPng(640, 480));
        File.WriteAllText(Path.Combine(trainLabels, "000001.txt"), "0 0.5 0.5 0.25 0.25", Encoding.UTF8);

        try
        {
            var result = TrainingAssetImporter.Import(source, "Example Game", Path.Combine(root, "workspaces"));
            var samples = new TrainingSampleStore(TrainingWorkspace.ForGame("Example Game", Path.Combine(root, "workspaces"))).List();

            Assert.Single(samples);
            Assert.Equal(640, samples[0].ImageWidth);
            Assert.Equal(480, samples[0].ImageHeight);
            Assert.Equal(0, samples[0].Labels[0].ClassId);
            Assert.Equal(Path.Combine("images", "train", "000001.png"), samples[0].DatasetImagePath);
            var store = new TrainingSampleStore(TrainingWorkspace.ForGame("Example Game", Path.Combine(root, "workspaces")));
            store.UpdateLabels(samples[0].Id, [new TrainingLabel
            {
                ClassId = 0,
                CenterX = 0.25,
                CenterY = 0.25,
                Width = 0.2,
                Height = 0.2,
            }], TrainingWorkspace.ForGame("Example Game", Path.Combine(root, "workspaces")).LoadDefinitions());
            Assert.Contains("0 0.25 0.25", File.ReadAllText(
                Path.Combine(result.WorkspacePath, "dataset", "labels", "train", "000001.txt")));
            Assert.DoesNotContain(result.Warnings, warning => warning.Contains("unsupported", StringComparison.OrdinalIgnoreCase));
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
}

#endif
