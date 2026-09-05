// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Core;
using Tript.Detection;
using Tript.Obs;
using Tript.Recorder;
using Xunit;
using System.Threading.Tasks;

namespace Tript.Recorder.Tests;

// The detection host's wiring, tested against fakes so no frame source, ONNX model or background
// thread is involved: detections from the detector become bookmarks on the active recording, with
// the right definition matched to the result, with bookmarks driven by net-count increases and never
// from a definition without a BookmarkType. The fake detector records its Start/Stop calls so a
// game switch is observable.
[Collection(RecorderRecordingCollection.Name)]
public sealed class DetectionHostTests
{
    private static readonly DateTime Origin = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);

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

    // Registers a live frame source (the recorder's setup) and returns a fresh detector backed by
    // the given definitions.
    private static FakeDetector WithFrameSource(Dictionary<string, List<EventDefinition>> definitions)
    {
        FrameSourceRegistry.SetResolver(() => new FakeFrameSource());
        return new FakeDetector(definitions);
    }

    // ---- recording-backed tests ----

    // A detection with a matching trigger definition and an active recording writes a bookmark of
    // the definition's type.
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

    // The bookmark time is an offset from the recording's start, not a wall-clock time: a session
    // started now, with a detection now, carries a bookmark offset of (near) zero. A wall-clock
    // timestamp would be the full epoch value, which is the failure this assertion catches.
    [Fact]
    public void Detections_BookmarkTimeIsAnOffsetFromTheRecordingStart()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] = [Trigger(0)],
        });
        var recording = new FakeRecordingSession { StartTime = DateTime.Now };

        using (new ActiveRecordingScope(recording))
        using (var host = new DetectionHost(detector, detector.DefinitionSource))
        {
            Assert.True(host.Start("Overwatch"));
            detector.RaiseDetections(Box(0));

            var bookmark = Assert.Single(recording.Bookmarks);
            Assert.True(bookmark.Time >= TimeSpan.Zero, $"bookmark offset {bookmark.Time} is negative");
            Assert.True(bookmark.Time < TimeSpan.FromSeconds(10),
                $"bookmark offset {bookmark.Time} is not an offset from the session start");
        }
    }

    // The definition is matched by ClassId — the join key events.json already uses. A detection of
    // class 0 must land on the class-0 definition's bookmark type, not a neighbouring class's.
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
            detector.RaiseDetections(Box(0)); // an elimination icon

            var bookmark = Assert.Single(recording.Bookmarks);
            Assert.Equal(BookmarkType.Kill, bookmark.Type);
        }
    }

    // A detection whose definition has no BookmarkType is detected but never bookmarked — that is
    // the design, not a bug.
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

    // A stable count does not create another bookmark.
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

    // An exclusion-type definition (no BookmarkType) fires without a bookmark.
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

    // An exclusion is a cycle-level veto, not merely an event that happens to carry no bookmark
    // type. It suppresses a trigger detected in the same inference batch.
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

    // Suppression is scoped to one detector batch. A clean trigger in the next cycle is still
    // eligible for bookmarking.
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

    // A detection whose ClassId no definition covers is dropped before count processing:
    // the model can emit classes events.json says nothing about, and there is no bookmark type to
    // give them. This guard is DetectionHost's, and it shipped without a test.
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

    // ---- lifecycle tests (no recording needed) ----

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

    // A game switch stops the old detector and starts the new — observable through the fake's
    // Start/Stop counts.
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

    // A Start that declines still tears the previous game's run down. The teardown sits before the
    // decision to start, so every refusing path — no model, no frame source — goes through it; a
    // teardown reached only on the succeeding path would leave the old game's detector subscribed and
    // writing its bookmarks into the new game's recording.
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

    // Starting the same game twice is idempotent — no restart, no double subscription.
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

    // Disposing the host stops the detector, like a Stop.
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

    // ---- the fakes ----

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

        public event Action<List<DetectionResult>>? DetectionsAvailable;

        public void Start(string gameId)
        {
            StartedGameId = gameId;
            StartCount++;
            if (DetectionsOnStart is { } detections)
                DetectionsAvailable?.Invoke(detections.ToList());
        }

        public void Stop() => StopCount++;

        internal void RaiseDetections(params DetectionResult[] detections)
            => DetectionsAvailable?.Invoke(detections.ToList());

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

        public List<EventDefinition> LoadEventDefinitions(string gameId)
        {
            Loaded.Add(gameId);
            return _definitions.TryGetValue(gameId, out var definitions) ? definitions : [];
        }
    }

    private sealed class FakeRecordingSession : IRecordingSession
    {
        public DateTime StartTime { get; init; } = Origin;

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
