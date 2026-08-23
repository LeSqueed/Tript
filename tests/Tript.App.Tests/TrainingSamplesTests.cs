// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

#if TRIPT_TRAINING

using Tript.App.Training;
using Tript.Detection;
using Xunit;

namespace Tript.App.Tests;

public sealed class TrainingSamplesTests
{
    [Fact]
    public void Save_writes_one_full_frame_sidecar_with_all_labels()
    {
        var root = Path.Combine(Path.GetTempPath(), "tript-training-sample-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "recording.mp4");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(source, [1]);

        try
        {
            var workspace = TrainingWorkspace.ForGame("Example", Path.Combine(root, "workspaces"));
            var store = new TrainingSampleStore(workspace);
            var labels = new List<TrainingLabel>
            {
                new() { ClassId = 0, CenterX = 0.25, CenterY = 0.25, Width = 0.2, Height = 0.2 },
                new() { ClassId = 1, CenterX = 0.7, CenterY = 0.7, Width = 0.1, Height = 0.1 },
            };
            var definitions = new List<EventDefinition>
            {
                new() { Id = 10, Name = "First", ClassId = 0, Type = EventType.Trigger },
                new() { Id = 20, Name = "Second", ClassId = 1, Type = EventType.Exclusion },
            };

            var sample = store.Save(source, 4.5, 1920, 1080, labels, [137, 80, 78, 71], definitions);

            Assert.Equal(24, sample.Id.Length);
            Assert.Equal(2, sample.Labels.Count);
            Assert.True(File.Exists(Path.Combine(workspace.SamplesPath, sample.ImageFile)));
            Assert.True(File.Exists(Path.Combine(workspace.SamplesPath, sample.Id + ".json")));
            Assert.Equal(sample.Id, TrainingSampleStore.SampleId(source, 4.5));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Save_rejects_unknown_and_out_of_bounds_labels()
    {
        var root = Path.Combine(Path.GetTempPath(), "tript-training-labels-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var definitions = new List<EventDefinition>
            {
                new() { Id = 1, Name = "Known", ClassId = 0, Type = EventType.Trigger },
            };
            var unknown = new TrainingLabel { ClassId = 9, CenterX = 0.5, CenterY = 0.5, Width = 0.2, Height = 0.2 };
            var outside = new TrainingLabel { ClassId = 0, CenterX = 0.95, CenterY = 0.5, Width = 0.2, Height = 0.2 };

            Assert.Contains("unknown classId", TrainingLabelValidator.FindError([unknown], definitions));
            Assert.Contains("outside", TrainingLabelValidator.FindError([outside], definitions));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Save_allows_an_unlabeled_pending_sample_for_later_annotation()
    {
        var root = Path.Combine(Path.GetTempPath(), "tript-training-empty-sample-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "recording.mp4");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(source, [1]);

        try
        {
            var workspace = TrainingWorkspace.ForGame("Example", Path.Combine(root, "workspaces"));
            var sample = new TrainingSampleStore(workspace).Save(source, 1, 1280, 720, [], [137, 80],
                [new EventDefinition { Id = 1, Name = "Known", ClassId = 0, Type = EventType.Trigger }]);

            Assert.Empty(sample.Labels);
            Assert.Empty(new TrainingSampleStore(workspace).List()[0].Labels);
            Assert.Contains("at least one label", TrainingLabelValidator.FindError([], [
                new EventDefinition { Id = 1, Name = "Known", ClassId = 0, Type = EventType.Trigger },
            ])!);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RemapClassIds_updates_unlabeled_and_labeled_sample_metadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "tript-training-remap-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "recording.mp4");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(source, [1]);

        try
        {
            var workspace = TrainingWorkspace.ForGame("Example", Path.Combine(root, "workspaces"));
            var store = new TrainingSampleStore(workspace);
            var sample = store.Save(source, 1, 1280, 720,
                [new TrainingLabel { ClassId = 2, CenterX = 0.5, CenterY = 0.5, Width = 0.2, Height = 0.2 }],
                [137, 80],
                [new EventDefinition { Id = 1, Name = "First", ClassId = 0, Type = EventType.Trigger },
                 new EventDefinition { Id = 2, Name = "Third", ClassId = 2, Type = EventType.Trigger }]);

            store.RemapClassIds(new Dictionary<int, int> { [0] = 0, [2] = 1 });

            Assert.Equal(1, store.LoadById(sample.Id).Labels.Single().ClassId);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RemapClassIds_rejects_deleting_an_event_with_labels()
    {
        var root = Path.Combine(Path.GetTempPath(), "tript-training-remap-reject-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "recording.mp4");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(source, [1]);

        try
        {
            var workspace = TrainingWorkspace.ForGame("Example", Path.Combine(root, "workspaces"));
            var store = new TrainingSampleStore(workspace);
            store.Save(source, 1, 1280, 720,
                [new TrainingLabel { ClassId = 1, CenterX = 0.5, CenterY = 0.5, Width = 0.2, Height = 0.2 }],
                [137, 80],
                [new EventDefinition { Id = 1, Name = "First", ClassId = 0, Type = EventType.Trigger },
                 new EventDefinition { Id = 2, Name = "Second", ClassId = 1, Type = EventType.Trigger }]);

            var exception = Assert.Throws<InvalidDataException>(() =>
                store.RemapClassIds(new Dictionary<int, int> { [0] = 0 }));

            Assert.Contains("labeled samples", exception.Message);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LabelSuggestions_skip_existing_overlap_and_duplicate_predictions()
    {
        var existing = new List<TrainingLabel>
        {
            new() { ClassId = 0, CenterX = 0.2, CenterY = 0.2, Width = 0.2, Height = 0.2 },
        };
        var detections = new List<DetectionResult>
        {
            new() { ClassId = 1, Confidence = 0.8f, X = 0.12f, Y = 0.12f, Width = 0.2f, Height = 0.2f },
            new() { ClassId = 1, Confidence = 0.9f, X = 0.6f, Y = 0.6f, Width = 0.1f, Height = 0.1f },
            new() { ClassId = 1, Confidence = 0.7f, X = 0.601f, Y = 0.601f, Width = 0.1f, Height = 0.1f },
        };

        var suggestions = TrainingLabelSuggestionFilter.Merge(existing, detections);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal(0.9f, suggestion.Confidence);
        Assert.Equal(1, suggestion.Label.ClassId);
        Assert.Equal(0.65, suggestion.Label.CenterX, 3);
    }

    [Fact]
    public void SubtractorReferences_require_an_existing_trigger()
    {
        var valid = new EventDefinition { Id = 1, ClassId = 0, Name = "Elimination", Type = EventType.Trigger };
        var subtractor = new EventDefinition
        {
            Id = 2,
            ClassId = 1,
            Name = "Turret",
            Type = EventType.Subtractor,
            SubtractsEventId = 1,
        };

        TrainingEventValidator.ValidateSubtractorReferences([valid, subtractor]);

        subtractor.SubtractsEventId = 99;
        var exception = Assert.Throws<InvalidDataException>(() =>
            TrainingEventValidator.ValidateSubtractorReferences([valid, subtractor]));
        Assert.Contains("missing event", exception.Message);
    }
}

#endif
