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
    public void Group_region_overrides_an_events_own_region_when_validating_labels()
    {
        var definitions = new List<EventDefinition>
        {
            new()
            {
                Id = 1, Name = "Grouped", ClassId = 0, Type = EventType.Trigger,
                RegionGroupId = 7,
                ScreenRegionX = 0.05f, ScreenRegionY = 0.05f,
                ScreenRegionW = 0.3f, ScreenRegionH = 0.3f,
            },
        };
        var groups = new List<TrainingRegionGroup>
        {
            new()
            {
                Id = 7, Name = "Right HUD", ScreenRegionX = 0.6f,
                ScreenRegionY = 0.1f, ScreenRegionW = 0.3f, ScreenRegionH = 0.3f,
            },
        };
        var insideOwnRegion = new TrainingLabel
        {
            ClassId = 0, CenterX = 0.2, CenterY = 0.2, Width = 0.1, Height = 0.1,
        };
        var insideGroupRegion = new TrainingLabel
        {
            ClassId = 0, CenterX = 0.7, CenterY = 0.2, Width = 0.1, Height = 0.1,
        };

        Assert.Contains("outside", TrainingLabelValidator.FindError(
            [insideOwnRegion], definitions, regionGroups: groups));
        Assert.Null(TrainingLabelValidator.FindError(
            [insideGroupRegion], definitions, regionGroups: groups));
    }

    [Fact]
    public void Empty_group_region_overrides_an_events_region_with_the_full_frame()
    {
        var definition = new EventDefinition
        {
            Id = 1, Name = "Grouped", ClassId = 0, Type = EventType.Trigger,
            RegionGroupId = 7,
            ScreenRegionX = 0.1f, ScreenRegionY = 0.1f,
            ScreenRegionW = 0.2f, ScreenRegionH = 0.2f,
        };
        var label = new TrainingLabel
        {
            ClassId = 0, CenterX = 0.8, CenterY = 0.8, Width = 0.1, Height = 0.1,
        };

        Assert.Null(TrainingLabelValidator.FindError([label], [definition],
            regionGroups: [new TrainingRegionGroup { Id = 7, Name = "Full frame" }]));
        Assert.Contains("missing region group", TrainingLabelValidator.FindError(
            [label], [definition], regionGroups: [])!);
    }

    [Fact]
    public void Region_groups_round_trip_and_materialize_effective_event_regions()
    {
        var root = Path.Combine(Path.GetTempPath(), "tript-training-groups-" + Guid.NewGuid().ToString("N"));
        try
        {
            var workspace = TrainingWorkspace.ForGame("Example", root);
            var groups = new List<TrainingRegionGroup>
            {
                new()
                {
                    Id = 4, Name = "Feed", ScreenRegionX = 0.4f, ScreenRegionY = 0.2f,
                    ScreenRegionW = 0.5f, ScreenRegionH = 0.3f,
                },
            };
            workspace.SaveRegionGroups(groups);

            var loaded = Assert.Single(workspace.LoadRegionGroups());
            Assert.Equal("Feed", loaded.Name);
            var materialized = Assert.Single(TrainingRegionResolver.MaterializeEffectiveRegions(
                [new EventDefinition
                {
                    Id = 1, Name = "Kill", ClassId = 0, Type = EventType.Trigger,
                    RegionGroupId = 4,
                }], groups));
            Assert.Equal(0.4f, materialized.ScreenRegionX);
            Assert.Equal(0.5f, materialized.ScreenRegionW);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Fixed_position_update_synchronizes_every_sample_of_that_class()
    {
        var root = Path.Combine(Path.GetTempPath(), "tript-training-fixed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var workspace = TrainingWorkspace.ForGame("Example", Path.Combine(root, "workspaces"));
            var store = new TrainingSampleStore(workspace);
            var definitions = new List<EventDefinition>
            {
                new() { Id = 1, Name = "Fixed", ClassId = 0, Type = EventType.Trigger, FixedPosition = true },
            };
            var source = Path.Combine(root, "recording.mp4");
            File.WriteAllBytes(source, [1]);
            var first = store.Save(source, 1, 1920, 1080,
                [new TrainingLabel { ClassId = 0, CenterX = 0.2, CenterY = 0.2, Width = 0.1, Height = 0.1 }],
                [137, 80, 78, 71], definitions);
            var second = store.Save(source, 2, 1920, 1080,
                [new TrainingLabel { ClassId = 0, CenterX = 0.8, CenterY = 0.8, Width = 0.2, Height = 0.2 }],
                [137, 80, 78, 71], definitions);

            var moved = new TrainingLabel { ClassId = 0, CenterX = 0.5, CenterY = 0.5, Width = 0.15, Height = 0.15 };
            store.UpdateLabels(first.Id, [moved], definitions);

            var reloaded = store.List().ToList();
            Assert.Equal(0.5, reloaded.Single(candidate => candidate.Id == second.Id).Labels.Single().CenterX, 6);
            Assert.Equal(0.5, reloaded.Single(candidate => candidate.Id == second.Id).Labels.Single().CenterY, 6);
            Assert.Equal(0.5, definitions.Single().FixedLabelCenterX);
            Assert.Equal(0.15, definitions.Single().FixedLabelWidth);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UpdateLabels_ignores_unrelated_legacy_corruption()
    {
        var root = Path.Combine(Path.GetTempPath(), "tript-training-legacy-label-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var workspace = TrainingWorkspace.ForGame("Example", Path.Combine(root, "workspaces"));
            var store = new TrainingSampleStore(workspace);
            var source = Path.Combine(root, "recording.mp4");
            File.WriteAllBytes(source, [1]);
            var originalDefinitions = new List<EventDefinition>
            {
                new() { Id = 1, Name = "Current", ClassId = 0, Type = EventType.Trigger },
                new() { Id = 2, Name = "Removed", ClassId = 1, Type = EventType.Trigger },
            };
            var target = store.Save(source, 1, 1920, 1080,
                [new TrainingLabel { ClassId = 0, CenterX = 0.2, CenterY = 0.2, Width = 0.1, Height = 0.1 }],
                [137, 80], originalDefinitions);
            var corrupt = store.Save(source, 2, 1920, 1080,
                [new TrainingLabel { ClassId = 1, CenterX = 0.7, CenterY = 0.7, Width = 0.1, Height = 0.1 }],
                [137, 80], originalDefinitions);
            var currentDefinitions = new List<EventDefinition>
            {
                new() { Id = 1, Name = "Current", ClassId = 0, Type = EventType.Trigger },
            };

            store.UpdateLabels(target.Id,
                [new TrainingLabel { ClassId = 0, CenterX = 0.4, CenterY = 0.4, Width = 0.1, Height = 0.1 }],
                currentDefinitions);

            Assert.Equal(0.4, store.LoadById(target.Id).Labels.Single().CenterX, 6);
            Assert.Equal(1, store.LoadById(corrupt.Id).Labels.Single().ClassId);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
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
    public void RemapClassIds_strips_labels_of_a_deleted_event_and_remaps_the_rest()
    {
        var root = Path.Combine(Path.GetTempPath(), "tript-training-remap-strip-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "recording.mp4");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(source, [1]);

        try
        {
            var workspace = TrainingWorkspace.ForGame("Example", Path.Combine(root, "workspaces"));
            var store = new TrainingSampleStore(workspace);
            store.Save(source, 1, 1280, 720,
                [
                    new TrainingLabel { ClassId = 0, CenterX = 0.2, CenterY = 0.2, Width = 0.1, Height = 0.1 },
                    new TrainingLabel { ClassId = 2, CenterX = 0.6, CenterY = 0.6, Width = 0.1, Height = 0.1 },
                ],
                [137, 80],
                [new EventDefinition { Id = 1, Name = "First", ClassId = 0, Type = EventType.Trigger },
                 new EventDefinition { Id = 2, Name = "Second", ClassId = 1, Type = EventType.Trigger },
                 new EventDefinition { Id = 3, Name = "Deleted", ClassId = 2, Type = EventType.Trigger }]);

            var result = store.RemapClassIds(new Dictionary<int, int> { [0] = 0 });

            var labels = store.LoadById(store.List().Single().Id).Labels;
            Assert.Single(labels);
            Assert.Equal(0, labels[0].ClassId);
            Assert.Equal(1, result.RemovedLabelCount);
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

        var definitions = new List<EventDefinition>
        {
            new() { Id = 1, ClassId = 1, Name = "Class", Type = EventType.Trigger },
        };
        var suggestions = TrainingLabelSuggestionFilter.Merge(existing, detections, definitions);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal(0.9f, suggestion.Confidence);
        Assert.Equal(1, suggestion.Label.ClassId);
        Assert.Equal(0.65, suggestion.Label.CenterX, 3);
    }

    [Fact]
    public void LabelSuggestions_skip_boxes_outside_the_events_screen_region()
    {
        var definitions = new List<EventDefinition>
        {
            new() { Id = 1, ClassId = 1, Name = "Class", Type = EventType.Trigger,
                ScreenRegionX = 0.5f, ScreenRegionY = 0.5f, ScreenRegionW = 0.3f, ScreenRegionH = 0.3f },
        };
        var detections = new List<DetectionResult>
        {
            // Fully inside the region.
            new() { ClassId = 1, Confidence = 0.9f, X = 0.55f, Y = 0.55f, Width = 0.1f, Height = 0.1f },
            // Center inside but the box bleeds out of the region.
            new() { ClassId = 1, Confidence = 0.8f, X = 0.7f, Y = 0.5f, Width = 0.2f, Height = 0.1f },
            // Entirely outside the region.
            new() { ClassId = 1, Confidence = 0.7f, X = 0.1f, Y = 0.1f, Width = 0.1f, Height = 0.1f },
        };

        var suggestions = TrainingLabelSuggestionFilter.Merge([], detections, definitions);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal(0.60, suggestion.Label.CenterX, 3);
        Assert.Equal(0.10, suggestion.Label.Width, 3);
    }

    [Fact]
    public void LabelSuggestions_use_the_fixed_geometry_for_fixed_position_events()
    {
        var definitions = new List<EventDefinition>
        {
            new()
            {
                Id = 1, ClassId = 1, Name = "Fixed", Type = EventType.Trigger,
                FixedPosition = true,
                FixedLabelCenterX = 0.5, FixedLabelCenterY = 0.5,
                FixedLabelWidth = 0.1, FixedLabelHeight = 0.05,
            },
        };
        var detections = new List<DetectionResult>
        {
            new() { ClassId = 1, Confidence = 0.9f, X = 0.3f, Y = 0.3f, Width = 0.2f, Height = 0.2f },
        };

        var suggestions = TrainingLabelSuggestionFilter.Merge([], detections, definitions);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal(0.5, suggestion.Label.CenterX, 6);
        Assert.Equal(0.5, suggestion.Label.CenterY, 6);
        Assert.Equal(0.1, suggestion.Label.Width, 6);
        Assert.Equal(0.05, suggestion.Label.Height, 6);
    }

    [Fact]
    public void LabelSuggestions_accept_fixed_geometry_inside_region_when_prediction_is_outside()
    {
        var definition = new EventDefinition
        {
            Id = 1, ClassId = 1, Name = "Fixed", Type = EventType.Trigger,
            FixedPosition = true,
            FixedLabelCenterX = 0.6, FixedLabelCenterY = 0.6,
            FixedLabelWidth = 0.1, FixedLabelHeight = 0.1,
            ScreenRegionX = 0.5f, ScreenRegionY = 0.5f,
            ScreenRegionW = 0.3f, ScreenRegionH = 0.3f,
        };
        var prediction = new DetectionResult
        {
            ClassId = 1, Confidence = 0.9f, X = 0.1f, Y = 0.1f, Width = 0.1f, Height = 0.1f,
        };

        Assert.Single(TrainingLabelSuggestionFilter.Merge([], [prediction], [definition]));
    }

    [Fact]
    public void LabelSuggestions_reject_fixed_geometry_outside_region_when_prediction_is_inside()
    {
        var definition = new EventDefinition
        {
            Id = 1, ClassId = 1, Name = "Fixed", Type = EventType.Trigger,
            FixedPosition = true,
            FixedLabelCenterX = 0.2, FixedLabelCenterY = 0.2,
            FixedLabelWidth = 0.1, FixedLabelHeight = 0.1,
            ScreenRegionX = 0.5f, ScreenRegionY = 0.5f,
            ScreenRegionW = 0.3f, ScreenRegionH = 0.3f,
        };
        var prediction = new DetectionResult
        {
            ClassId = 1, Confidence = 0.9f, X = 0.55f, Y = 0.55f, Width = 0.1f, Height = 0.1f,
        };

        Assert.Empty(TrainingLabelSuggestionFilter.Merge([], [prediction], [definition]));
    }

    [Fact]
    public void LabelSuggestions_skip_fixed_position_events_without_initialized_geometry()
    {
        var definitions = new List<EventDefinition>
        {
            new() { Id = 1, ClassId = 1, Name = "Unset", Type = EventType.Trigger, FixedPosition = true },
        };
        var detections = new List<DetectionResult>
        {
            new() { ClassId = 1, Confidence = 0.9f, X = 0.3f, Y = 0.3f, Width = 0.2f, Height = 0.2f },
        };

        var suggestions = TrainingLabelSuggestionFilter.Merge([], detections, definitions);

        Assert.Empty(suggestions);
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
