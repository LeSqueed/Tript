// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Core;
using Tript.Detection;
using Tript.Obs;
using Tript.Recorder;
using Xunit;

namespace Tript.Recorder.Tests;

// The detection host's wiring, tested against fakes so no frame source, ONNX model or background
// thread is involved: detections from the detector become bookmarks on the active recording, with
// the right definition matched to the result, coalesced inside a definition's lifetime and never
// from a definition without a BookmarkType. The fake detector records its Start/Stop calls so a
// game switch is observable.
[Collection(RecorderRecordingCollection.Name)]
public sealed class DetectionHostTests
{
    private static readonly DateTime Origin = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);

    private static EventDefinition Trigger(int classId, BookmarkType? bookmarkType = BookmarkType.Kill,
        int? lifetimeMs = null) => new()
    {
        ClassId = classId,
        Name = "event" + classId,
        Type = EventType.Trigger,
        BookmarkType = bookmarkType,
        LifetimeMs = lifetimeMs,
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
        using (var host = new DetectionHost(detector, detector.DefinitionSource, cleanupInterval: TimeSpan.Zero))
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
        using (var host = new DetectionHost(detector, detector.DefinitionSource, cleanupInterval: TimeSpan.Zero))
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
        using (var host = new DetectionHost(detector, detector.DefinitionSource, cleanupInterval: TimeSpan.Zero))
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
        using (var host = new DetectionHost(detector, detector.DefinitionSource, cleanupInterval: TimeSpan.Zero))
        {
            Assert.True(host.Start("Overwatch"));
            detector.RaiseDetections(Box(0));

            Assert.Empty(recording.Bookmarks);
        }
    }

    // Detections within the definition's lifetime are the same event and coalesce into one bookmark.
    [Fact]
    public void Detections_WithinLifetime_CoalesceIntoOneBookmark()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] = [Trigger(0, lifetimeMs: 5000)],
        });
        var recording = new FakeRecordingSession();

        using (new ActiveRecordingScope(recording))
        using (var host = new DetectionHost(detector, detector.DefinitionSource, cleanupInterval: TimeSpan.Zero))
        {
            Assert.True(host.Start("Overwatch"));
            detector.RaiseDetections(Box(0));
            detector.RaiseDetections(Box(0));
            detector.RaiseDetections(Box(0));

            Assert.Single(recording.Bookmarks);
        }
    }

    // After the definition's lifetime elapses, the same event again is a second bookmark. The
    // host's cleanup timer retires the stale instance so it cannot swallow the next detection.
    [Fact]
    public void Detections_AfterLifetimeElapsed_ProduceASecondBookmark()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] = [Trigger(0, lifetimeMs: 100)],
        });
        var recording = new FakeRecordingSession();

        using (new ActiveRecordingScope(recording))
        using (var host = new DetectionHost(detector, detector.DefinitionSource,
            cleanupInterval: TimeSpan.FromMilliseconds(50)))
        {
            Assert.True(host.Start("Overwatch"));
            detector.RaiseDetections(Box(0));

            // The host's cleanup timer retires the stale instance after its lifetime (100ms).
            Thread.Sleep(300);
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
        using (var host = new DetectionHost(detector, detector.DefinitionSource, cleanupInterval: TimeSpan.Zero))
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
        using (var host = new DetectionHost(detector, detector.DefinitionSource, cleanupInterval: TimeSpan.Zero))
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
        using (var host = new DetectionHost(detector, detector.DefinitionSource, cleanupInterval: TimeSpan.Zero))
        {
            Assert.True(host.Start("Overwatch"));
            detector.RaiseDetections(Box(1));
            detector.RaiseDetections(Box(0));

            var bookmark = Assert.Single(recording.Bookmarks);
            Assert.Equal(BookmarkType.Kill, bookmark.Type);
        }
    }

    // A detection whose ClassId no definition covers is dropped before the cooldown tracker sees it:
    // the model can emit classes events.json says nothing about, and there is no bookmark type to
    // give them. Ported from _pending/DetectionSessionTests, whose subsystem never landed — this
    // guard is DetectionHost's, and it shipped without a test.
    [Fact]
    public void Detections_WithNoDefinitionForTheirClass_AreDropped()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] = [Trigger(0, BookmarkType.Kill)],
        });
        var recording = new FakeRecordingSession();

        using (new ActiveRecordingScope(recording))
        using (var host = new DetectionHost(detector, detector.DefinitionSource, cleanupInterval: TimeSpan.Zero))
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
        using var host = new DetectionHost(detector, detector.DefinitionSource, cleanupInterval: TimeSpan.Zero);

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
        using var host = new DetectionHost(detector, detector.DefinitionSource, cleanupInterval: TimeSpan.Zero);

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

        using var host = new DetectionHost(detector, detector.DefinitionSource, cleanupInterval: TimeSpan.Zero);

        Assert.True(host.Start("Overwatch"));
        Assert.True(host.IsRunning);
        Assert.Equal("Overwatch", host.CurrentGameId);
        Assert.Equal("Overwatch", host.LastStartedGameId);
        Assert.Equal(1, detector.StartCount);
        Assert.Equal("Overwatch", detector.StartedGameId);
    }

    [Fact]
    public void Stop_StopsTheDetectorAndClearsTheCurrentGame()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] = [Trigger(0)],
        });

        using var host = new DetectionHost(detector, detector.DefinitionSource, cleanupInterval: TimeSpan.Zero);
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

        using var host = new DetectionHost(detector, detector.DefinitionSource, cleanupInterval: TimeSpan.Zero);

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
    // writing its bookmarks into the new game's recording. Ported from
    // _pending/DetectorTeardownOnGameSwitchTests, whose subsystem never landed.
    [Fact]
    public void Start_ForAGameItRefuses_StillStopsThePreviousDetector()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] = [Trigger(0)],
        });

        using var host = new DetectionHost(detector, detector.DefinitionSource, cleanupInterval: TimeSpan.Zero);

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

        using var host = new DetectionHost(detector, detector.DefinitionSource, cleanupInterval: TimeSpan.Zero);

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

        var host = new DetectionHost(detector, detector.DefinitionSource, cleanupInterval: TimeSpan.Zero);
        Assert.True(host.Start("Overwatch"));

        host.Dispose();

        Assert.Equal(1, detector.StopCount);
    }

    // ---- cleanup-thread lifecycle ----
    //
    // These are the only tests that arm the real cleanup thread (every other test passes
    // TimeSpan.Zero to keep it out of the way), because the defect they pin is the join itself.

    // The cleanup thread takes the host's state lock every cycle, so joining it while that lock is
    // held can never succeed: the join burns its full two-second budget and then gives up. A stop
    // that takes anywhere near that long is the regression.
    [Fact]
    public void Stop_WithTheCleanupThreadRunning_DoesNotStallOnTheJoin()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] = [Trigger(0)],
        });

        using var host = new DetectionHost(detector, detector.DefinitionSource,
            cleanupInterval: TimeSpan.FromMilliseconds(20));
        Assert.True(host.Start("Overwatch"));

        // Let the thread reach its wait at least once, so the join has something real to wait for.
        Thread.Sleep(60);

        var elapsed = Time(host.Stop);

        Assert.False(host.IsRunning);
        Assert.True(elapsed < TimeSpan.FromSeconds(1), $"Stop took {elapsed}");
    }

    [Fact]
    public void Dispose_WithTheCleanupThreadRunning_DoesNotStallOnTheJoin()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] = [Trigger(0)],
        });

        var host = new DetectionHost(detector, detector.DefinitionSource,
            cleanupInterval: TimeSpan.FromMilliseconds(20));
        Assert.True(host.Start("Overwatch"));
        Thread.Sleep(60);

        var elapsed = Time(host.Dispose);

        Assert.Equal(1, detector.StopCount);
        Assert.True(elapsed < TimeSpan.FromSeconds(1), $"Dispose took {elapsed}");
    }

    // The game-switch path stops the old run from inside Start, which is the third caller of the
    // same join.
    [Fact]
    public void Start_SwitchingGamesWithTheCleanupThreadRunning_DoesNotStallOnTheJoin()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] = [Trigger(0)],
            ["Valorant"] = [Trigger(0)],
        });

        using var host = new DetectionHost(detector, detector.DefinitionSource,
            cleanupInterval: TimeSpan.FromMilliseconds(20));
        Assert.True(host.Start("Overwatch"));
        Thread.Sleep(60);

        var switched = false;
        var elapsed = Time(() => switched = host.Start("Valorant"));

        Assert.True(switched);
        Assert.Equal("Valorant", host.CurrentGameId);
        Assert.True(elapsed < TimeSpan.FromSeconds(1), $"The game switch took {elapsed}");
    }

    // The falsifier for the join-under-the-lock defect. The stall needs the cleanup thread to be
    // queued on the state lock at the instant the stop takes it, so the test manufactures exactly
    // that: a zero-length cleanup interval keeps the thread cycling through the lock without
    // pausing, and hammer threads keep it contended. Joining under that lock costs the join's full
    // two-second budget whenever it lands; joining outside it cannot stall at all.
    [Fact]
    public void RepeatedStopsUnderLockContention_NeverStall()
    {
        var detector = WithFrameSource(new()
        {
            ["Overwatch"] = [Trigger(0)],
        });

        using var host = new DetectionHost(detector, detector.DefinitionSource,
            cleanupInterval: TimeSpan.FromTicks(1));

        using var stopHammer = new CancellationTokenSource();
        var hammers = new List<Thread>();
        for (var i = 0; i < 3; i++)
        {
            var hammer = new Thread(() =>
            {
                while (!stopHammer.IsCancellationRequested)
                    _ = host.IsRunning;
            })
            { IsBackground = true };
            hammers.Add(hammer);
            hammer.Start();
        }

        try
        {
            var elapsed = Time(() =>
            {
                for (var i = 0; i < 15; i++)
                {
                    Assert.True(host.Start("Overwatch"));
                    Thread.Sleep(1);
                    host.Stop();
                }
            });

            Assert.True(elapsed < TimeSpan.FromSeconds(2), $"15 start/stop cycles took {elapsed}");
        }
        finally
        {
            stopHammer.Cancel();
            foreach (var hammer in hammers)
                hammer.Join(TimeSpan.FromSeconds(5));
        }
    }

    private static TimeSpan Time(Action action)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        action();
        return started.Elapsed;
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

        public event Action<List<DetectionResult>>? DetectionsAvailable;

        public void Start(string gameId)
        {
            StartedGameId = gameId;
            StartCount++;
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

        public void AddBookmark(Bookmark bookmark) => Bookmarks.Add(bookmark);
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
