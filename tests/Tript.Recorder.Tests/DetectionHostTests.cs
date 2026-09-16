// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Core;
using Tript.Detection;
using Tript.Obs;
using Tript.Recorder;
using Xunit;
using System.Threading.Tasks;

namespace Tript.Recorder.Tests;

[Collection(RecorderRecordingCollection.Name)]
public sealed class DetectionHostTests
{
    private static readonly DateTime Origin = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static EventDefinition Trigger(int classId, BookmarkType? bookmarkType = BookmarkType.Kill,
        bool includeInAutoClips = false) => new()
    {
        Id = classId,
        ClassId = classId,
        Name = "event" + classId,
        Type = EventType.Trigger,
        BookmarkType = bookmarkType,
        IncludeInAutoClips = includeInAutoClips,
    };

    private static EventDefinition Subtractor(int id, int classId, int targetId) => new()
    {
        Id = id,
        ClassId = classId,
        Name = "subtractor" + classId,
        Type = EventType.Subtractor,
        SubtractsEventId = targetId,
    };

    private static EventDefinition OcrEvent(int id, EventType type = EventType.Trigger,
        int? targetId = null, BookmarkType? bookmarkType = BookmarkType.Kill) => new()
    {
        Id = id,
        Name = "ocr" + id,
        Type = type,
        DetectionKind = DetectionKind.Ocr,
        BookmarkType = bookmarkType,
        SubtractsEventId = targetId,
        Ocr = new OcrEventDefinition
        {
            Patterns = [new OcrPatternDefinition { LanguageTag = "en", Template = "KILL {player}" }],
            Tracking = new OcrTrackingDefinition
            {
                ConfirmationFrames = 1,
                MinimumStableMilliseconds = 0,
                ExpireAfterMissingMilliseconds = 0,
            },
        },
    };

    private static OcrMatch Text(int eventId, string text, float y = 0.1f, float confidence = 0.95f) => new()
    {
        EventId = eventId,
        Text = text,
        NormalizedText = OcrTextNormalizer.Normalize(text),
        LanguageTag = "en",
        SegmentId = "feed",
        Confidence = confidence,
        X = 0.1f,
        Y = y,
        Width = 0.4f,
        Height = 0.05f,
    };

    private static DetectionResult Box(int classId, float x = 0.4f, float y = 0.3f,
        float w = 0.12f, float h = 0.06f) => new()
    {
        ClassId = classId,
        Confidence = 0.9f,
        X = x,
        Y = y,
        Width = w,
        Height = h,
        Timestamp = Origin,
    };

    private static FakeDetector WithFrameSource(Dictionary<string, List<EventDefinition>> definitions)
    {
        FrameSourceRegistry.SetResolver(() => new FakeFrameSource());
        return new FakeDetector(definitions);
    }

    [Fact]
    public void Detections_WithMatchingTriggerDefinition_WriteABookmarkOfTheDefinitionsType()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] = [Trigger(0, BookmarkType.Kill)],
        });
        var recording = new FakeRecordingSession();

        using (new ActiveRecordingScope(recording))
        using (var host = new DetectionHost(detector, detector.DefinitionSource))
        {
            Assert.True(host.Start("Overwatch"));
            detector.RaiseDetections(Box(0));

            var bookmark = Assert.Single(recording.Bookmarks);
            Assert.Equal(BookmarkType.Kill, bookmark.Type);
        }
    }

    [Fact]
    public void Detections_BookmarkTimeIsAnOffsetFromTheRecordingStart()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] = [Trigger(0)],
        });
        var recording = new FakeRecordingSession { StartTimeUtc = DateTime.UtcNow };
        var detection = Box(0);
        detection.Timestamp = recording.StartTimeUtc;

        using (new ActiveRecordingScope(recording))
        using (var host = new DetectionHost(detector, detector.DefinitionSource))
        {
            Assert.True(host.Start("Overwatch"));
            detector.RaiseDetections(detection);

            var bookmark = Assert.Single(recording.Bookmarks);
            Assert.True(bookmark.Time >= TimeSpan.Zero, $"bookmark offset {bookmark.Time} is negative");
            Assert.True(bookmark.Time < TimeSpan.FromSeconds(10),
                $"bookmark offset {bookmark.Time} is not an offset from the session start");
        }
    }

    [Fact]
    public void Detections_MatchTheDefinitionByClassId_NotAnotherClass()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] =
            [
                Trigger(0, BookmarkType.Kill),
                Trigger(1, BookmarkType.Death),
            ],
        });
        var recording = new FakeRecordingSession();

        using (new ActiveRecordingScope(recording))
        using (var host = new DetectionHost(detector, detector.DefinitionSource))
        {
            Assert.True(host.Start("Overwatch"));
            detector.RaiseDetections(Box(0));

            var bookmark = Assert.Single(recording.Bookmarks);
            Assert.Equal(BookmarkType.Kill, bookmark.Type);
        }
    }

    [Fact]
    public void Detections_WithNoBookmarkType_ProduceNoBookmark()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] = [Trigger(0, bookmarkType: null)],
        });
        var recording = new FakeRecordingSession();

        using (new ActiveRecordingScope(recording))
        using (var host = new DetectionHost(detector, detector.DefinitionSource))
        {
            Assert.True(host.Start("Overwatch"));
            detector.RaiseDetections(Box(0));

            Assert.Empty(recording.Bookmarks);
        }
    }

    [Fact]
    public void Detections_StableCount_CreatesOneBookmark()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] = [Trigger(0)],
        });
        var recording = new FakeRecordingSession();

        using (new ActiveRecordingScope(recording))
        using (var host = new DetectionHost(detector, detector.DefinitionSource))
        {
            Assert.True(host.Start("Overwatch"));
            detector.RaiseDetections(Box(0));
            detector.RaiseDetections(Box(0));
            detector.RaiseDetections(Box(0));

            Assert.Single(recording.Bookmarks);
        }
    }

    [Fact]
    public void Detections_OnlyDefinitionsMarkedForAutoClipsReachTheClipCallback()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] =
            [
                Trigger(0, BookmarkType.Kill, includeInAutoClips: true),
                Trigger(1, BookmarkType.Death),
            ],
        });
        var recording = new FakeRecordingSession();
        var automaticBookmarks = new List<Bookmark>();

        using (new ActiveRecordingScope(recording))
        using (var host = new DetectionHost(detector, detector.DefinitionSource,
                   onAutomaticClipBookmark: automaticBookmarks.Add))
        {
            Assert.True(host.Start("Overwatch"));
            detector.RaiseDetections(Box(0), Box(1, x: 0.7f));

            var bookmark = Assert.Single(automaticBookmarks);
            Assert.Same(recording.Bookmarks[0], bookmark);
            Assert.Equal(2, recording.Bookmarks.Count);
        }
    }

    [Fact]
    public void Detections_IncreasedCount_CreatesOnlyTheIncrease()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] = [Trigger(0)],
        });
        var recording = new FakeRecordingSession();

        using (new ActiveRecordingScope(recording))
        using (var host = new DetectionHost(detector, detector.DefinitionSource))
        {
            Assert.True(host.Start("Overwatch"));
            detector.RaiseDetections(Box(0, x: 0.1f));
            detector.RaiseDetections(Box(0, x: 0.1f), Box(0, x: 0.6f));

            Assert.Equal(2, recording.Bookmarks.Count);
        }
    }

    [Fact]
    public void Detections_EmptyFrameResetsTheCount()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] = [Trigger(0)],
        });
        var recording = new FakeRecordingSession();

        using (new ActiveRecordingScope(recording))
        using (var host = new DetectionHost(detector, detector.DefinitionSource))
        {
            Assert.True(host.Start("Overwatch"));
            detector.RaiseDetections(Box(0));
            detector.RaiseDetections();
            detector.RaiseDetections(Box(0));

            Assert.Equal(2, recording.Bookmarks.Count);
        }
    }

    [Fact]
    public void OcrTrigger_PersistentTextCreatesOneBookmark()
    {
        var detector = WithFrameSource(new() { ["Overwatch"] = [OcrEvent(7)] });
        var recording = new FakeRecordingSession();

        using (new ActiveRecordingScope(recording))
        using (var host = new DetectionHost(detector, detector.DefinitionSource))
        {
            Assert.True(host.Start("Overwatch"));
            detector.RaiseOcr(Text(7, "KILL AMON"));
            detector.RaiseOcr(Text(7, "KILL AMON"));

            Assert.Single(recording.Bookmarks);
        }
    }

    [Fact]
    public void OcrTrigger_DuplicateRecognitionInOneBatchCountsOnce()
    {
        var detector = WithFrameSource(new() { ["Overwatch"] = [OcrEvent(7)] });
        var recording = new FakeRecordingSession();

        using (new ActiveRecordingScope(recording))
        using (var host = new DetectionHost(detector, detector.DefinitionSource))
        {
            Assert.True(host.Start("Overwatch"));
            var match = Text(7, "KILL AMON");
            detector.RaiseOcr(match, match);

            Assert.Single(recording.Bookmarks);
        }
    }

    [Fact]
    public void OcrExclusion_SuppressesObjectTriggerInSameBatch()
    {
        var exclusion = OcrEvent(7, EventType.Exclusion, bookmarkType: null);
        exclusion.Ocr!.Tracking.ConfirmationFrames = 2;
        exclusion.Ocr.Tracking.MinimumStableMilliseconds = 1000;
        var detector = WithFrameSource(new() { ["Overwatch"] = [Trigger(0), exclusion] });
        var recording = new FakeRecordingSession();

        using (new ActiveRecordingScope(recording))
        using (var host = new DetectionHost(detector, detector.DefinitionSource))
        {
            Assert.True(host.Start("Overwatch"));
            detector.RaiseBatch([Box(0)], [Text(7, "KILL CAM")]);
            detector.RaiseBatch([Box(0)], [], Origin.AddMilliseconds(1));

            Assert.Empty(recording.Bookmarks);
        }
    }

    [Fact]
    public void OcrSubtractor_SubtractsFromObjectTriggerInSameBatch()
    {
        var subtractor = OcrEvent(7, EventType.Subtractor, targetId: 0, bookmarkType: null);
        subtractor.Ocr!.Tracking.ConfirmationFrames = 2;
        subtractor.Ocr.Tracking.MinimumStableMilliseconds = 1000;
        var detector = WithFrameSource(new() { ["Overwatch"] = [Trigger(0), subtractor] });
        var recording = new FakeRecordingSession();

        using (new ActiveRecordingScope(recording))
        using (var host = new DetectionHost(detector, detector.DefinitionSource))
        {
            Assert.True(host.Start("Overwatch"));
            detector.RaiseBatch([Box(0)], [Text(7, "KILL TURRET")]);

            Assert.Empty(recording.Bookmarks);
        }
    }

    [Fact]
    public void OcrExclusion_LowConfidenceMisread_DoesNotSuppressObjectTrigger()
    {
        var exclusion = OcrEvent(7, EventType.Exclusion, bookmarkType: null);
        var detector = WithFrameSource(new() { ["Overwatch"] = [Trigger(0), exclusion] });
        var recording = new FakeRecordingSession { StartTimeUtc = Origin };

        using (new ActiveRecordingScope(recording))
        using (var host = new DetectionHost(detector, detector.DefinitionSource))
        {
            Assert.True(host.Start("Overwatch"));
            detector.RaiseBatch([Box(0)], [Text(7, "KILL CAM", confidence: 0.55f)]);

            Assert.Single(recording.Bookmarks);
        }
    }

    [Fact]
    public void ObjectSubtractor_SubtractsFromOcrTriggerInSameBatch()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] = [OcrEvent(7), Subtractor(8, 0, 7)],
        });
        var recording = new FakeRecordingSession();

        using (new ActiveRecordingScope(recording))
        using (var host = new DetectionHost(detector, detector.DefinitionSource))
        {
            Assert.True(host.Start("Overwatch"));
            detector.RaiseBatch([Box(0)], [Text(7, "KILL AMON")]);

            Assert.Empty(recording.Bookmarks);
        }
    }

    [Fact]
    public void Detections_ImplausibleBurstInOneCycle_IsClamped()
    {
        var detector = WithFrameSource(new() { ["Overwatch"] = [Trigger(0)] });
        var recording = new FakeRecordingSession { StartTimeUtc = Origin };

        var boxes = (from column in new[] { 0.05f, 0.20f, 0.35f, 0.50f }
                     from row in new[] { 0.05f, 0.20f, 0.35f }
                     select Box(0, column, row, 0.05f, 0.05f)).ToArray();
        Assert.Equal(12, boxes.Length);

        using (new ActiveRecordingScope(recording))
        using (var host = new DetectionHost(detector, detector.DefinitionSource))
        {
            Assert.True(host.Start("Overwatch"));
            detector.RaiseDetections(boxes);

            Assert.Equal(8, recording.Bookmarks.Count);
        }
    }

    [Fact]
    public void Detections_ExclusionDefinition_ProduceNoBookmark()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] =
            [
                new EventDefinition
                {
                    ClassId = 1,
                    Name = "Death Spectating",
                    Type = EventType.Exclusion,
                    BookmarkType = null,
                },
            ],
        });
        var recording = new FakeRecordingSession();

        using (new ActiveRecordingScope(recording))
        using (var host = new DetectionHost(detector, detector.DefinitionSource))
        {
            Assert.True(host.Start("Overwatch"));
            detector.RaiseDetections(Box(1));

            Assert.Empty(recording.Bookmarks);
        }
    }

    [Fact]
    public void Detections_ExclusionInTheSameBatch_SuppressesTriggers()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] =
            [
                Trigger(0, BookmarkType.Kill),
                new EventDefinition
                {
                    ClassId = 1,
                    Name = "Death Spectating",
                    Type = EventType.Exclusion,
                },
            ],
        });
        var recording = new FakeRecordingSession();

        using (new ActiveRecordingScope(recording))
        using (var host = new DetectionHost(detector, detector.DefinitionSource))
        {
            Assert.True(host.Start("Overwatch"));
            detector.RaiseDetections(Box(0), Box(1));

            Assert.Empty(recording.Bookmarks);
        }
    }

    [Fact]
    public void Detections_ExclusionInAnEarlierBatch_DoesNotSuppressLaterTriggers()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] =
            [
                Trigger(0, BookmarkType.Kill),
                new EventDefinition
                {
                    ClassId = 1,
                    Name = "Death Spectating",
                    Type = EventType.Exclusion,
                },
            ],
        });
        var recording = new FakeRecordingSession();

        using (new ActiveRecordingScope(recording))
        using (var host = new DetectionHost(detector, detector.DefinitionSource))
        {
            Assert.True(host.Start("Overwatch"));
            detector.RaiseDetections(Box(1));
            detector.RaiseDetections(Box(0));

            var bookmark = Assert.Single(recording.Bookmarks);
            Assert.Equal(BookmarkType.Kill, bookmark.Type);
        }
    }

    [Fact]
    public void Detections_ExclusionDoesNotResetAnExistingNetCount()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] =
            [
                Trigger(0, BookmarkType.Kill),
                new EventDefinition
                {
                    ClassId = 1,
                    Name = "Death Spectating",
                    Type = EventType.Exclusion,
                },
            ],
        });
        var recording = new FakeRecordingSession();

        using (new ActiveRecordingScope(recording))
        using (var host = new DetectionHost(detector, detector.DefinitionSource))
        {
            Assert.True(host.Start("Overwatch"));
            detector.RaiseDetections(Box(0));
            detector.RaiseDetections(Box(0), Box(1));
            detector.RaiseDetections(Box(0));

            Assert.Single(recording.Bookmarks);
        }
    }

    [Fact]
    public void Detections_TwoSubtractorInstances_RemoveTwoTriggerBookmarksInTheSameBatch()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] = [Trigger(0), Subtractor(1, 1, 0)],
        });
        var recording = new FakeRecordingSession();

        using (new ActiveRecordingScope(recording))
        using (var host = new DetectionHost(detector, detector.DefinitionSource))
        {
            Assert.True(host.Start("Overwatch"));
            detector.RaiseDetections(
                Box(0, x: 0.1f), Box(0, x: 0.6f),
                Box(1, x: 0.1f), Box(1, x: 0.6f));

            Assert.Empty(recording.Bookmarks);
        }
    }

    [Fact]
    public void Detections_DifferentSubtractorDefinitionsAreAdditive()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] = [Trigger(0), Subtractor(1, 1, 0), Subtractor(2, 2, 0)],
        });
        var recording = new FakeRecordingSession();

        using (new ActiveRecordingScope(recording))
        using (var host = new DetectionHost(detector, detector.DefinitionSource))
        {
            Assert.True(host.Start("Overwatch"));
            detector.RaiseDetections(Box(0), Box(1), Box(2));

            Assert.Empty(recording.Bookmarks);
        }
    }

    [Fact]
    public void Detections_SubtractionHoldsTheNetCountUntilItRises()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] = [Trigger(0), Subtractor(1, 1, 0)],
        });
        var recording = new FakeRecordingSession();

        using (new ActiveRecordingScope(recording))
        using (var host = new DetectionHost(detector, detector.DefinitionSource))
        {
            Assert.True(host.Start("Overwatch"));
            detector.RaiseDetections(Box(0, x: 0.1f));
            detector.RaiseDetections(
                Box(0, x: 0.1f), Box(0, x: 0.6f), Box(1));
            detector.RaiseDetections(
                Box(0, x: 0.1f), Box(0, x: 0.6f), Box(0, x: 0.8f), Box(1));

            Assert.Equal(2, recording.Bookmarks.Count);
        }
    }

    [Fact]
    public void Detections_DuplicateSubtractorBoxesCountOnce()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] = [Trigger(0), Subtractor(1, 1, 0)],
        });
        var recording = new FakeRecordingSession();

        using (new ActiveRecordingScope(recording))
        using (var host = new DetectionHost(detector, detector.DefinitionSource))
        {
            Assert.True(host.Start("Overwatch"));
            detector.RaiseDetections(Box(0, x: 0.1f), Box(0, x: 0.6f),
                Box(1, x: 0.1f), Box(1, x: 0.101f));

            Assert.Single(recording.Bookmarks);
        }
    }

    [Fact]
    public void Detections_SubtractionIsLimitedToTheCurrentBatch()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] = [Trigger(0), Subtractor(1, 1, 0)],
        });
        var recording = new FakeRecordingSession();

        using (new ActiveRecordingScope(recording))
        using (var host = new DetectionHost(detector, detector.DefinitionSource))
        {
            Assert.True(host.Start("Overwatch"));
            detector.RaiseDetections(Box(0), Box(1));
            detector.RaiseDetections(Box(0, x: 0.8f));

            Assert.Single(recording.Bookmarks);
        }
    }

    [Fact]
    public void Detections_WithNoDefinitionForTheirClass_AreDropped()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] = [Trigger(0, BookmarkType.Kill)],
        });
        var recording = new FakeRecordingSession();

        using (new ActiveRecordingScope(recording))
        using (var host = new DetectionHost(detector, detector.DefinitionSource))
        {
            Assert.True(host.Start("Overwatch"));
            detector.RaiseDetections(Box(7));

            Assert.Empty(recording.Bookmarks);
        }
    }

    [Fact]
    public void Start_WithNoModel_IsRefusedAndStartsNothing()
    {
        var detector = new FakeDetector(new());
        using var host = new DetectionHost(detector, detector.DefinitionSource);

        Assert.False(host.Start("Overwatch"));
        Assert.False(host.IsRunning);
        Assert.Null(host.CurrentGameId);
        Assert.Null(host.LastStartedGameId);
        Assert.Equal(0, detector.StartCount);
        Assert.Equal(0, detector.StopCount);
    }

    [Fact]
    public void Start_WithoutRegisteredFrameSource_IsRefusedAndStartsNothing()
    {
        FrameSourceRegistry.Reset();
        var detector = new FakeDetector(new()
        {
            ["Overwatch"] = [Trigger(0)],
        });
        using var host = new DetectionHost(detector, detector.DefinitionSource);

        Assert.False(host.Start("Overwatch"));
        Assert.False(host.IsRunning);
        Assert.Equal(0, detector.StartCount);
        Assert.Equal(0, detector.StopCount);
    }

    [Fact]
    public void Start_WithModelAndFrameSource_StartsTheDetectorForTheGame()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] = [Trigger(0)],
        });

        using var host = new DetectionHost(detector, detector.DefinitionSource);

        Assert.True(host.Start("Overwatch"));
        Assert.True(host.IsRunning);
        Assert.Equal("Overwatch", host.CurrentGameId);
        Assert.Equal("Overwatch", host.LastStartedGameId);
        Assert.Equal(1, detector.StartCount);
        Assert.Equal("Overwatch", detector.StartedGameId);
    }

    [Fact]
    public void Start_DoesNotLoseABatchEmittedImmediatelyByTheDetector()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] = [Trigger(0)],
        });
        detector.DetectionsOnStart = [Box(0)];
        var recording = new FakeRecordingSession();

        using (new ActiveRecordingScope(recording))
        using (var host = new DetectionHost(detector, detector.DefinitionSource))
        {
            Assert.True(host.Start("Overwatch"));
            Assert.Single(recording.Bookmarks);
        }
    }

    [Fact]
    public void Stop_StopsTheDetectorAndClearsTheCurrentGame()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] = [Trigger(0)],
        });

        using var host = new DetectionHost(detector, detector.DefinitionSource);
        Assert.True(host.Start("Overwatch"));

        host.Stop();

        Assert.False(host.IsRunning);
        Assert.Null(host.CurrentGameId);
        Assert.Null(host.LastStartedGameId);
        Assert.Equal(1, detector.StopCount);
    }

    [Fact]
    public void Start_WithDifferentGameWhileRunning_StopsTheOldAndStartsTheNew()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] = [Trigger(0)],
            ["Valorant"] = [Trigger(0)],
        });

        using var host = new DetectionHost(detector, detector.DefinitionSource);

        Assert.True(host.Start("Overwatch"));
        Assert.True(host.Start("Valorant"));

        Assert.True(host.IsRunning);
        Assert.Equal("Valorant", host.CurrentGameId);
        Assert.Equal("Valorant", host.LastStartedGameId);
        Assert.Equal(1, detector.StopCount);
        Assert.Equal(2, detector.StartCount);
        Assert.Equal("Valorant", detector.StartedGameId);
    }

    [Fact]
    public void Start_ForAGameItRefuses_StillStopsThePreviousDetector()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] = [Trigger(0)],
        });

        using var host = new DetectionHost(detector, detector.DefinitionSource);

        Assert.True(host.Start("Overwatch"));
        Assert.False(host.Start("A Game That Ships No Model"));

        Assert.False(host.IsRunning);
        Assert.Null(host.CurrentGameId);
        Assert.Equal(1, detector.StopCount);
    }

    [Fact]
    public void Start_WithTheSameGameWhileRunning_LeavesTheDetectorRunning()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] = [Trigger(0)],
        });

        using var host = new DetectionHost(detector, detector.DefinitionSource);

        Assert.True(host.Start("Overwatch"));
        Assert.True(host.Start("Overwatch"));

        Assert.True(host.IsRunning);
        Assert.Equal(0, detector.StopCount);
        Assert.Equal(1, detector.StartCount);
    }

    [Fact]
    public void Dispose_StopsTheDetector()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] = [Trigger(0)],
        });

        var host = new DetectionHost(detector, detector.DefinitionSource);
        Assert.True(host.Start("Overwatch"));

        host.Dispose();

        Assert.Equal(1, detector.StopCount);
    }

    [Fact]
    public async Task Stop_WaitsForAnInFlightDetectionBeforeReturning()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] = [Trigger(0)],
        });
        var recording = new FakeRecordingSession { BlockBookmark = true };

        using (new ActiveRecordingScope(recording))
        using (var host = new DetectionHost(detector, detector.DefinitionSource))
        {
            Assert.True(host.Start("Overwatch"));
            var detection = Task.Run(() => detector.RaiseDetections(Box(0)));
            Assert.True(recording.BookmarkEntered.Wait(TimeSpan.FromSeconds(5)));

            var stopping = Task.Run(host.Stop);
            Assert.NotSame(stopping, await Task.WhenAny(stopping, Task.Delay(100)));

            recording.ReleaseBookmark.Set();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            await detection.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class FakeDetector : IVisualEventDetector
    {
        internal FakeDetector(Dictionary<string, List<EventDefinition>> definitions)
        {
            DefinitionSource = new FakeDefinitionSource(definitions);
        }

        internal ITrackDefinitionSource DefinitionSource { get; }

        internal string? StartedGameId;

        internal int StartCount;

        internal int StopCount;

        internal DetectionResult[]? DetectionsOnStart;

        public event Action<DetectionBatch>? DetectionsAvailable;

        public void Start(string gameId)
        {
            StartedGameId = gameId;
            StartCount++;
            if (DetectionsOnStart is { } detections)
                RaiseDetections(detections);
        }

        public void Stop() => StopCount++;

        internal void RaiseDetections(params DetectionResult[] detections)
            => DetectionsAvailable?.Invoke(new DetectionBatch
            {
                FrameTimestamp = detections.FirstOrDefault()?.Timestamp ?? DateTime.UtcNow,
                ObjectDetections = detections.ToList(),
            });

        internal void RaiseOcr(params OcrMatch[] matches) => RaiseBatch([], matches);

        internal void RaiseBatch(IEnumerable<DetectionResult> detections, IEnumerable<OcrMatch> matches,
            DateTime? frameTimestamp = null)
            => DetectionsAvailable?.Invoke(new DetectionBatch
            {
                FrameTimestamp = frameTimestamp ?? Origin,
                ObjectDetections = detections.ToList(),
                OcrMatches = matches.ToList(),
            });

        public void Dispose()
        {
        }
    }

    private sealed class FakeDefinitionSource : ITrackDefinitionSource
    {
        private readonly Dictionary<string, List<EventDefinition>> _definitions;

        internal FakeDefinitionSource(Dictionary<string, List<EventDefinition>> definitions)
            => _definitions = definitions;

        internal HashSet<string> CheckedForModel { get; } = [];

        internal HashSet<string> Loaded { get; } = [];

        public bool HasModelForGame(string gameId)
        {
            CheckedForModel.Add(gameId);
            return _definitions.ContainsKey(gameId);
        }

        public bool HasDetectionBundleForGame(string gameId) => HasModelForGame(gameId);

        public List<EventDefinition> LoadEventDefinitions(string gameId)
        {
            Loaded.Add(gameId);
            return _definitions.TryGetValue(gameId, out var definitions) ? definitions : [];
        }
    }

    private sealed class FakeRecordingSession : IRecordingSession
    {
        public DateTime StartTimeUtc { get; init; } = Origin;

        public List<Bookmark> Bookmarks { get; } = [];

        internal bool BlockBookmark { get; init; }

        internal ManualResetEventSlim BookmarkEntered { get; } = new(false);

        internal ManualResetEventSlim ReleaseBookmark { get; } = new(false);

        public void AddBookmark(Bookmark bookmark)
        {
            if (BlockBookmark)
            {
                BookmarkEntered.Set();
                ReleaseBookmark.Wait();
            }

            Bookmarks.Add(bookmark);
        }

        public bool RemoveBookmark(Guid id) => Bookmarks.RemoveAll(bookmark => bookmark.Id == id) > 0;
    }

    private sealed class FakeFrameSource : IFrameSource
    {
        public IFrameSubscription Subscribe(FramePixelFormat format, int width, int height,
            FrameCallback callback, uint frameRateDivisor) => new FakeSubscription();

        public VideoTiming? GetVideoTiming() => new(60, 1);

        private sealed class FakeSubscription : IFrameSubscription
        {
            public void Dispose()
            {
            }
        }
    }
}
