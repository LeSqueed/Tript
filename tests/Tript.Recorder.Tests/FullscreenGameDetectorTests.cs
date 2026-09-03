// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Recorder;
using Xunit;
using Tript.TestSupport;
using Tript.Core;

namespace Tript.Recorder.Tests;

public sealed class FullscreenGameDetectorTests
{
    [Fact]
    public void DetectorIsCandidateDiscovery_NotAGameDetector()
    {
        Assert.False(typeof(IGameDetector).IsAssignableFrom(typeof(FullscreenGameDetector)));
        Assert.Null(typeof(FullscreenGameDetector).GetEvent("GameStarted"));
        Assert.Null(typeof(FullscreenGameDetector).GetEvent("GameStopped"));
    }

    [Fact]
    public void Poll_EmitsNormalizedTypedCandidate()
    {
        var path = GamePath("Alpha.EXE");
        using var detector = Detector([], () => new(41, "Alpha.EXE", path));
        FullscreenGameCandidate? found = null;
        detector.CandidateFound += candidate => found = candidate;

        detector.PollOnce();
        detector.PollOnce();
        detector.WaitForCallbacks();

        Assert.Equal(new FullscreenGameCandidate(41, "Alpha", path), found);
    }

    [Fact]
    public void CandidateExecutableComesFromItsFullPath()
    {
        var path = GamePath("alpha.exe");
        using var detector = Detector([], () => new(41, "spoofed.exe", path));
        FullscreenGameCandidate? found = null;
        detector.CandidateFound += candidate => found = candidate;

        detector.PollOnce();
        detector.PollOnce();
        detector.WaitForCallbacks();

        Assert.Equal("alpha", Assert.IsType<FullscreenGameCandidate>(found).Executable);
    }

    [Fact]
    public void EveryPollRevalidatesAndClearsCandidateThatLosesEligibility()
    {
        FullscreenGameCandidate? probed = new(41, "alpha", GamePath("alpha.exe"));
        using var detector = Detector([], () => probed);
        var found = new List<FullscreenGameCandidate>();
        var cleared = new List<FullscreenGameCandidate>();
        detector.CandidateFound += found.Add;
        detector.CandidateCleared += cleared.Add;

        detector.PollOnce();
        detector.PollOnce();
        probed = null;
        detector.PollOnce();
        detector.WaitForCallbacks();

        Assert.Single(found);
        Assert.Equal(found, cleared);
    }

    [Fact]
    public void ForegroundCandidateChange_ClearsOldBeforeFindingNew()
    {
        FullscreenGameCandidate? probed = new(41, "alpha", GamePath("alpha.exe"));
        using var detector = Detector([], () => probed);
        var transitions = new List<string>();
        detector.CandidateFound += candidate => transitions.Add("found:" + candidate.ProcessId);
        detector.CandidateCleared += candidate => transitions.Add("cleared:" + candidate.ProcessId);

        detector.PollOnce();
        detector.PollOnce();
        probed = new(42, "beta", GamePath("beta.exe"));
        detector.PollOnce();
        detector.PollOnce();
        detector.WaitForCallbacks();

        Assert.Equal(["found:41", "cleared:41", "found:42"], transitions);
    }

    [Fact]
    public void KnownBasenameTargetIsIgnored()
    {
        using var detector = Detector(
            [new("alpha-id", "ALPHA.EXE")],
            () => new(41, "alpha", GamePath("alpha.exe")));
        var found = new List<FullscreenGameCandidate>();
        detector.CandidateFound += found.Add;

        detector.PollOnce();
        detector.PollOnce();
        detector.WaitForCallbacks();

        Assert.Empty(found);
    }

    [Fact]
    public void KnownPathTargetIgnoresOnlyTheConfiguredCanonicalPath()
    {
        var knownPath = GamePath("known", "alpha.exe");
        FullscreenGameCandidate? probed = new(41, "alpha", GamePath("other", "alpha.exe"));
        using var detector = Detector(
            [new("alpha-id", "alpha.exe", Path.Combine(Path.GetDirectoryName(knownPath)!, ".", "alpha.exe"))],
            () => probed);
        var found = new List<FullscreenGameCandidate>();
        detector.CandidateFound += found.Add;

        detector.PollOnce();
        detector.PollOnce();
        probed = new(42, "alpha", knownPath);
        detector.PollOnce();
        detector.WaitForCallbacks();

        Assert.Single(found);
        Assert.Equal(41, found[0].ProcessId);
    }

    [Fact]
    public void UpdatingKnownTargetsClearsAnActiveCandidateOnTheNextPoll()
    {
        var path = GamePath("alpha.exe");
        using var detector = Detector([], () => new(41, "alpha", path));
        var cleared = new List<FullscreenGameCandidate>();
        detector.CandidateCleared += cleared.Add;

        detector.PollOnce();
        detector.PollOnce();
        detector.WaitForCallbacks();
        detector.UpdateKnownTargets([new("alpha-id", "alpha.exe", path)]);
        detector.PollOnce();
        detector.WaitForCallbacks();

        Assert.Single(cleared);
    }

    [Fact]
    public void SubscriberExceptionDoesNotPreventOtherSubscribersOrClearing()
    {
        FullscreenGameCandidate? probed = new(41, "alpha", GamePath("alpha.exe"));
        using var detector = Detector([], () => probed);
        var notifications = 0;
        detector.CandidateFound += _ => throw new InvalidOperationException("test");
        detector.CandidateFound += _ => notifications++;
        detector.CandidateCleared += _ => notifications++;

        detector.PollOnce();
        detector.PollOnce();
        detector.WaitForCallbacks();
        probed = null;
        detector.PollOnce();
        detector.WaitForCallbacks();

        Assert.Equal(2, notifications);
    }

    [Fact]
    public void CandidateFoundRequiresTwoConsecutiveStablePolls()
    {
        FullscreenGameCandidate? probed = new(41, "alpha", GamePath("alpha.exe"), StartTime(1));
        using var detector = Detector([], () => probed);
        var found = new List<FullscreenGameCandidate>();
        detector.CandidateFound += found.Add;

        detector.PollOnce();
        detector.WaitForCallbacks();
        Assert.Empty(found);

        probed = null;
        detector.PollOnce();
        probed = new(41, "alpha", GamePath("alpha.exe"), StartTime(1));
        detector.PollOnce();
        detector.WaitForCallbacks();
        Assert.Empty(found);

        detector.PollOnce();
        detector.WaitForCallbacks();
        Assert.Single(found);
    }

    [Fact]
    public void ActiveCandidateClearsOnFirstMissingPoll()
    {
        FullscreenGameCandidate? probed = new(41, "alpha", GamePath("alpha.exe"), StartTime(1));
        using var detector = Detector([], () => probed);
        var cleared = new List<FullscreenGameCandidate>();
        detector.CandidateCleared += cleared.Add;

        detector.PollOnce();
        detector.PollOnce();
        detector.WaitForCallbacks();
        probed = null;
        detector.PollOnce();
        detector.WaitForCallbacks();

        Assert.Single(cleared);
    }

    [Fact]
    public async Task UpdateKnownTargets_InvalidatesProbeAndQueuedCandidate()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var candidate = new FullscreenGameCandidate(
            41, "alpha", GamePath("alpha.exe"), StartTime(1));
        var calls = 0;
        using var detector = Detector([], () =>
        {
            if (Interlocked.Increment(ref calls) == 2)
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            }
            return candidate;
        });
        var found = new List<FullscreenGameCandidate>();
        detector.CandidateFound += found.Add;

        detector.PollOnce();
        var stalePoll = Task.Run(detector.PollOnce);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        detector.UpdateKnownTargets([new("alpha-id", "alpha.exe")]);
        release.Set();
        await stalePoll.WaitAsync(TimeSpan.FromSeconds(5));
        detector.WaitForCallbacks();

        Assert.Empty(found);
    }

    [Fact]
    public async Task DisposeSuppressesQueuedCandidateCallbacks()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        FullscreenGameCandidate? probed =
            new(41, "alpha", GamePath("alpha.exe"), StartTime(1));
        var detector = Detector([], () => probed);
        var notifications = new List<string>();
        detector.CandidateFound += _ =>
        {
            notifications.Add("found");
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
        };
        detector.CandidateCleared += _ => notifications.Add("cleared");

        detector.PollOnce();
        detector.PollOnce();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        probed = null;
        detector.PollOnce();
        var disposing = Task.Run(detector.Dispose);
        Assert.True(SpinWait.SpinUntil(() => detector.IsDisposed, TimeSpan.FromSeconds(5)));
        release.Set();
        await disposing.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["found"], notifications);
    }

    [Fact]
    public async Task UpdateKnownTargetsSuppressesQueuedCandidateCallbacks()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        FullscreenGameCandidate? probed =
            new(41, "alpha", GamePath("alpha.exe"), StartTime(1));
        using var detector = Detector([], () => probed);
        var notifications = new List<string>();
        detector.CandidateFound += _ =>
        {
            notifications.Add("found");
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
        };
        detector.CandidateCleared += _ => notifications.Add("cleared");

        detector.PollOnce();
        detector.PollOnce();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        probed = null;
        detector.PollOnce();
        detector.UpdateKnownTargets([]);
        release.Set();
        await Task.Run(detector.WaitForCallbacks).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["found"], notifications);
    }

    [Fact]
    public async Task DisposeFromCandidateCallbackDoesNotDeadlock()
    {
        using var exited = new ManualResetEventSlim();
        var detector = Detector(
            [],
            () => new(41, "alpha", GamePath("alpha.exe"), StartTime(1)));
        detector.CandidateFound += _ =>
        {
            detector.Dispose();
            exited.Set();
        };

        detector.PollOnce();
        detector.PollOnce();

        Assert.True(await Task.Run(() => exited.Wait(TimeSpan.FromSeconds(5))));
    }

    [WindowsFact]
    public void WindowsCandidateCasingIsStable()
    {
        var path = GamePath("Alpha.exe");
        FullscreenGameCandidate? probed = new(41, "Alpha", path, StartTime(1));
        using var detector = Detector([], () => probed);
        var transitions = 0;
        detector.CandidateFound += _ => transitions++;
        detector.CandidateCleared += _ => transitions++;

        detector.PollOnce();
        detector.PollOnce();
        detector.WaitForCallbacks();
        probed = new(41, "ALPHA", path.ToUpperInvariant(), StartTime(1));
        detector.PollOnce();
        detector.WaitForCallbacks();

        Assert.Equal(1, transitions);
    }

    [Fact]
    public async Task Poll_DropsReentrantCalls()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        using var detector = Detector([], () =>
        {
            Interlocked.Increment(ref calls);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            return null;
        });

        var first = Task.Run(detector.PollOnce);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        detector.PollOnce();
        release.Set();
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, calls);
    }

    [WindowsTheory]
    [InlineData("C:\\Windows\\System32\\explorer.exe")]
    [InlineData("C:\\Windows\\SysWOW64\\something.exe")]
    [InlineData("C:\\Program Files\\WindowsApps\\Example\\game.exe")]
    public void SystemLocationsAreIgnored(string path)
        => Assert.True(FullscreenGameDetector.IsSystemExecutable(path));

    [WindowsFact]
    public void SystemPathPrefixDoesNotMatchASimilarlyNamedUserFolder()
        => Assert.False(FilePaths.IsUnder("C:\\WindowsGames\\game.exe", "C:\\Windows"));

    private static FullscreenGameDetector Detector(
        IEnumerable<GameDetectionTarget> targets,
        Func<FullscreenGameCandidate?> probe)
        => new(targets, probe, TimeSpan.FromHours(1));

    private static string GamePath(params string[] parts)
        => Path.GetFullPath(Path.Combine(["test-games", .. parts]));

    private static DateTimeOffset StartTime(int seconds)
        => new(2026, 1, 1, 0, 0, seconds, TimeSpan.Zero);
}
