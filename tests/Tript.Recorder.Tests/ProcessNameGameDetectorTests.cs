// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Recorder;
using Xunit;
using Tript.TestSupport;

namespace Tript.Recorder.Tests;

public sealed class ProcessNameGameDetectorTests
{
    [Fact]
    public void Poll_EmitsTypedProcessForMatchingTarget()
    {
        var path = GamePath("alpha.exe");
        using var detector = Detector(
            [new("alpha-id", "alpha.exe")],
            _ => [new(41, "ALPHA.EXE", path)]);
        DetectedGameProcess? started = null;
        detector.GameStarted += process => started = process;

        detector.PollOnce();
        detector.WaitForCallbacks();

        Assert.Equal(new DetectedGameProcess("alpha-id", 41, "ALPHA", path), started);
    }

    [Fact]
    public void Poll_GivesTheProbeTheCandidateExecutableNames()
    {
        IReadOnlySet<string>? candidates = null;
        using var detector = Detector(
            [new("alpha-id", "alpha.exe"), new("beta-id", "not-used.exe", GamePath("beta.exe"))],
            names =>
            {
                candidates = names;
                return [];
            });

        detector.PollOnce();

        Assert.NotNull(candidates);
        Assert.Equal(["alpha", "beta"], candidates.Order());
    }

    [Fact]
    public void PathTarget_MatchesOnlyItsCanonicalPath()
    {
        var trustedPath = GamePath("trusted", "alpha.exe");
        var otherPath = GamePath("other", "alpha.exe");
        using var detector = Detector(
            [new("alpha-id", "alpha.exe", Path.Combine(Path.GetDirectoryName(trustedPath)!, ".", "alpha.exe"))],
            _ => [new(41, "alpha", otherPath)]);
        var started = new List<DetectedGameProcess>();
        detector.GameStarted += started.Add;

        detector.PollOnce();
        detector.WaitForCallbacks();

        Assert.Empty(started);
    }

    [Fact]
    public void PathTarget_MatchesTheCanonicalEquivalentPath()
    {
        var path = GamePath("trusted", "alpha.exe");
        var configured = Path.Combine(Path.GetDirectoryName(path)!, ".", "alpha.exe");
        using var detector = Detector(
            [new("alpha-id", "not-used.exe", configured)],
            _ => [new(41, "alpha.exe", path)]);
        var started = new List<DetectedGameProcess>();
        detector.GameStarted += started.Add;

        detector.PollOnce();
        detector.WaitForCallbacks();

        Assert.Single(started);
    }

    [Fact]
    public void BasenameTarget_MatchesCaseInsensitivelyAndWithoutExeSuffix()
    {
        using var detector = Detector(
            [new("alpha-id", "ALPHA.EXE")],
            _ => [new(41, "alpha", GamePath("alpha.exe"))]);
        var started = new List<DetectedGameProcess>();
        detector.GameStarted += started.Add;

        detector.PollOnce();
        detector.WaitForCallbacks();

        Assert.Single(started);
    }

    [Fact]
    public void MalformedExplicitPath_IsRejectedRatherThanFallingBackToBasename()
    {
        Assert.Throws<ArgumentException>(() => Detector(
            [new("alpha-id", "alpha.exe", "invalid\0path")],
            _ => []));
    }

    [Fact]
    public void ProcessesAreTrackedAndStoppedPerPid()
    {
        IReadOnlyList<ProcessSnapshot> processes =
        [
            new(41, "alpha", GamePath("one", "alpha.exe")),
            new(42, "alpha", GamePath("two", "alpha.exe")),
        ];
        using var detector = Detector([new("alpha-id", "alpha.exe")], _ => processes);
        var started = new List<DetectedGameProcess>();
        var stopped = new List<DetectedGameProcess>();
        detector.GameStarted += started.Add;
        detector.GameStopped += stopped.Add;

        detector.PollOnce();
        processes = [processes[1]];
        detector.PollOnce();
        detector.WaitForCallbacks();

        Assert.Equal([41, 42], started.Select(process => process.ProcessId));
        Assert.Equal(41, Assert.Single(stopped).ProcessId);
    }

    [Fact]
    public void UpdateTargets_ReclassifiesARunningPidAsStopThenStart()
    {
        var process = new ProcessSnapshot(41, "alpha", GamePath("alpha.exe"));
        using var detector = Detector([new("old-id", "alpha.exe")], _ => [process]);
        var transitions = new List<string>();
        detector.GameStarted += found => transitions.Add("start:" + found.GameId);
        detector.GameStopped += gone => transitions.Add("stop:" + gone.GameId);

        detector.PollOnce();
        detector.WaitForCallbacks();
        detector.UpdateTargets([new("new-id", "alpha.exe")]);
        detector.PollOnce();
        detector.WaitForCallbacks();

        Assert.Equal(["start:old-id", "stop:old-id", "start:new-id"], transitions);
    }

    [Fact]
    public async Task UpdateTargets_InvalidatesAProbeUsingThePreviousTargetSet()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var path = GamePath("alpha.exe");
        var calls = 0;
        using var detector = Detector([new("old-id", "alpha.exe")], _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            }
            return [new(41, "alpha", path)];
        });
        var started = new List<DetectedGameProcess>();
        detector.GameStarted += started.Add;

        var stalePoll = Task.Run(detector.PollOnce);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        detector.UpdateTargets([new("new-id", "alpha.exe")]);
        release.Set();
        await stalePoll.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(started);

        detector.PollOnce();
        detector.WaitForCallbacks();
        Assert.Equal("new-id", Assert.Single(started).GameId);
    }

    [Fact]
    public async Task Poll_DropsReentrantCalls()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        using var detector = Detector([], _ =>
        {
            Interlocked.Increment(ref calls);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            return [];
        });

        var first = Task.Run(detector.PollOnce);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        detector.PollOnce();
        release.Set();
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, calls);
    }

    [Fact]
    public void SubscriberException_DoesNotPreventOtherSubscribersOrLaterPolls()
    {
        IReadOnlyList<ProcessSnapshot> processes =
            [new(41, "alpha", GamePath("alpha.exe"))];
        using var detector = Detector([new("alpha-id", "alpha.exe")], _ => processes);
        var notifications = 0;
        detector.GameStarted += _ => throw new InvalidOperationException("test");
        detector.GameStarted += _ => notifications++;
        detector.GameStopped += _ => notifications++;

        detector.PollOnce();
        processes = [];
        detector.PollOnce();
        detector.WaitForCallbacks();

        Assert.Equal(2, notifications);
    }

    [Fact]
    public void BasenameTarget_MatchesWhenPathInspectionIsDenied()
    {
        using var detector = Detector(
            [new("alpha-id", "alpha.exe")],
            _ => [new(41, "alpha", null, StartTime(1))]);
        var started = new List<DetectedGameProcess>();
        detector.GameStarted += started.Add;

        detector.PollOnce();
        detector.WaitForCallbacks();

        Assert.Equal(41, Assert.Single(started).ProcessId);
        Assert.Equal(string.Empty, started[0].ExecutablePath);
    }

    [Fact]
    public void ExactPathTarget_TransientPathFailureDoesNotStopTrackedProcess()
    {
        var path = GamePath("alpha.exe");
        var start = StartTime(1);
        IReadOnlyList<ProcessSnapshot> processes = [new(41, "alpha", path, start)];
        using var detector = Detector([new("alpha-id", "alpha.exe", path)], _ => processes);
        var transitions = new List<string>();
        detector.GameStarted += _ => transitions.Add("start");
        detector.GameStopped += _ => transitions.Add("stop");

        detector.PollOnce();
        detector.WaitForCallbacks();
        processes = [new(41, "alpha", null, start)];
        detector.PollOnce();
        detector.WaitForCallbacks();

        Assert.Equal(["start"], transitions);
    }

    [Fact]
    public void ReusedPid_StopsOldIdentityBeforeStartingNewIdentity()
    {
        var path = GamePath("alpha.exe");
        IReadOnlyList<ProcessSnapshot> processes = [new(41, "alpha", path, StartTime(1))];
        using var detector = Detector([new("alpha-id", "alpha.exe")], _ => processes);
        var transitions = new List<string>();
        detector.GameStarted += process => transitions.Add("start:" + process.ProcessStartTime);
        detector.GameStopped += process => transitions.Add("stop:" + process.ProcessStartTime);

        detector.PollOnce();
        detector.WaitForCallbacks();
        processes = [new(41, "alpha", path, StartTime(2))];
        detector.PollOnce();
        detector.WaitForCallbacks();

        Assert.Equal(
            [$"start:{StartTime(1)}", $"stop:{StartTime(1)}", $"start:{StartTime(2)}"],
            transitions);
    }

    [WindowsFact]
    public void WindowsPathAndExecutableCasingDoesNotCreateTransitions()
    {
        var path = GamePath("Alpha.exe");
        IReadOnlyList<ProcessSnapshot> processes = [new(41, "Alpha", path, StartTime(1))];
        using var detector = Detector([new("alpha-id", "ALPHA.EXE")], _ => processes);
        var transitions = 0;
        detector.GameStarted += _ => transitions++;
        detector.GameStopped += _ => transitions++;

        detector.PollOnce();
        detector.WaitForCallbacks();
        processes = [new(41, "ALPHA", path.ToUpperInvariant(), StartTime(1))];
        detector.PollOnce();
        detector.WaitForCallbacks();

        Assert.Equal(1, transitions);
    }

    [Fact]
    public async Task SlowSubscriber_DoesNotPreventPollingAndTransitionsStayOrdered()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        IReadOnlyList<ProcessSnapshot> processes =
            [new(41, "alpha", GamePath("alpha.exe"), StartTime(1))];
        using var detector = Detector([new("alpha-id", "alpha.exe")], _ => processes);
        var transitions = new List<string>();
        detector.GameStarted += _ =>
        {
            transitions.Add("start");
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
        };
        detector.GameStopped += _ => transitions.Add("stop");

        detector.PollOnce();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        processes = [];
        await Task.Run(detector.PollOnce).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(["start"], transitions);
        release.Set();
        detector.WaitForCallbacks();

        Assert.Equal(["start", "stop"], transitions);
    }

    [Fact]
    public async Task UpdateTargets_SuppressesQueuedStartsFromTheOldGeneration()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var detector = Detector(
            [new("alpha-id", "alpha.exe")],
            _ =>
            [
                new(41, "alpha", GamePath("one", "alpha.exe"), StartTime(1)),
                new(42, "alpha", GamePath("two", "alpha.exe"), StartTime(2)),
            ]);
        var started = new List<int>();
        detector.GameStarted += process =>
        {
            started.Add(process.ProcessId);
            if (process.ProcessId == 41)
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            }
        };

        detector.PollOnce();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        detector.UpdateTargets([]);
        release.Set();
        await Task.Run(detector.WaitForCallbacks).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([41], started);
    }

    [Fact]
    public async Task Dispose_WaitsForActiveCallbackAndSuppressesQueuedCallbacks()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var detector = Detector(
            [new("alpha-id", "alpha.exe")],
            _ =>
            [
                new(41, "alpha", GamePath("one", "alpha.exe"), StartTime(1)),
                new(42, "alpha", GamePath("two", "alpha.exe"), StartTime(2)),
            ]);
        var started = new List<int>();
        detector.GameStarted += process =>
        {
            started.Add(process.ProcessId);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
        };

        detector.PollOnce();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var disposing = Task.Run(detector.Dispose);
        Assert.True(SpinWait.SpinUntil(() => detector.IsDisposed, TimeSpan.FromSeconds(5)));
        release.Set();
        await disposing.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([41], started);
    }

    [Fact]
    public async Task DisposeFromCallback_DoesNotDeadlock()
    {
        using var exited = new ManualResetEventSlim();
        var detector = Detector(
            [new("alpha-id", "alpha.exe")],
            _ => [new(41, "alpha", GamePath("alpha.exe"), StartTime(1))]);
        detector.GameStarted += _ =>
        {
            detector.Dispose();
            exited.Set();
        };

        detector.PollOnce();

        Assert.True(await Task.Run(() => exited.Wait(TimeSpan.FromSeconds(5))));
    }

    [LinuxFact]
    public void ProtonProcess_MatchesTheDiscoveredLinuxPathAndStopsWhenItExits()
    {
        const string installed = "/home/u/.local/share/Steam/steamapps/common/Overwatch/Overwatch.exe";
        var files = new FakeProcessFiles(
            LinuxProcessIdentityTests.Line(@"Z:\home\u\.local\share\Steam\steamapps\common\Overwatch\Overwatch.exe"),
            "/opt/proton/files/bin/wine64-preloader");
        var running = true;
        using var detector = Detector(
            [new("overwatch-id", "Overwatch.exe", installed)],
            names =>
            {
                var identity = LinuxProcessIdentity.Read(files, 77)!;
                return running && names.Contains(ProcessNameGameDetector.NormalizeProcessName(identity.Executable))
                    ? [new(77, identity.Executable, identity.ExecutablePath, StartTime(1))]
                    : [];
            });
        var started = new List<DetectedGameProcess>();
        var stopped = new List<DetectedGameProcess>();
        detector.GameStarted += started.Add;
        detector.GameStopped += stopped.Add;

        detector.PollOnce();
        running = false;
        detector.PollOnce();
        detector.WaitForCallbacks();

        var game = Assert.Single(started);
        Assert.Equal(new DetectedGameProcess("overwatch-id", 77, "Overwatch", installed, StartTime(1)), game);
        Assert.Equal(game, Assert.Single(stopped));
    }

    [LinuxFact]
    public void LinuxPathTarget_ConfiguredThroughALinkMatchesTheProcessCanonicalPath()
    {
        var root = Directory.CreateTempSubdirectory("tript-detector-").FullName;
        try
        {
            var library = Directory.CreateDirectory(Path.Combine(root, "share", "Steam")).FullName;
            Directory.CreateSymbolicLink(Path.Combine(root, "steam"), library);
            var canonical = Path.Combine(library, "steamapps", "common", "Game", "game.x86_64");
            using var detector = Detector(
                [new("game-id", "game.x86_64", Path.Combine(root, "steam", "steamapps", "common", "Game", "game.x86_64"))],
                _ => [new(41, "game.x86_64", canonical)]);
            var started = new List<DetectedGameProcess>();
            detector.GameStarted += started.Add;

            detector.PollOnce();
            detector.WaitForCallbacks();

            Assert.Equal(canonical, Assert.Single(started).ExecutablePath);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static ProcessNameGameDetector Detector(
        IEnumerable<GameDetectionTarget> targets,
        Func<IReadOnlySet<string>, IReadOnlyList<ProcessSnapshot>> probe)
        => new(targets, probe, TimeSpan.FromHours(1));

    private static string GamePath(params string[] parts)
        => Path.GetFullPath(Path.Combine(["test-games", .. parts]));

    private static DateTimeOffset StartTime(int seconds)
        => new(2026, 1, 1, 0, 0, seconds, TimeSpan.Zero);
}
