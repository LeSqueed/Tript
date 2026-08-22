// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using Tript.Recorder;
using Xunit;

namespace Tript.Recorder.Tests;

// The minimal process watcher. These tests are careful not to depend on the real process list (they
// would be flaky), so the watcher is exercised through a process they control: this test process's
// own name, which is definitely running.
public class ProcessNameGameDetectorTests
{
    [Fact]
    public void Start_ReportsAGameAlreadyRunning_OnTheFirstPoll()
    {
        // The current test host's process name — "testhost" or "dotnet" — is running by
        // definition, so a catalogue listing it must produce a start event.
        var ownName = Path.GetFileNameWithoutExtension(Environment.ProcessPath!);
        var started = new List<string>();
        var stopped = 0;
        using var detector = new ProcessNameGameDetector(new[] { ownName }, pollInterval: TimeSpan.FromMilliseconds(10));
        detector.GameStarted += name => started.Add(name);
        detector.GameStopped += _ => Interlocked.Increment(ref stopped);

        detector.Start();

        // The first poll is immediate; a little settling gives the timer its first tick.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (started.Count == 0 && DateTime.UtcNow < deadline)
            Thread.Sleep(10);

        Assert.NotEmpty(started);
        Assert.Contains(started, name => string.Equals(name, ownName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AProcessNotInTheCatalogue_IsNotReported()
    {
        var ownName = Path.GetFileNameWithoutExtension(Environment.ProcessPath!);
        var started = new List<string>();
        using var detector = new ProcessNameGameDetector(new[] { "definitely-not-a-running-game-xyzzy" }, pollInterval: TimeSpan.FromMilliseconds(10));
        detector.GameStarted += name => started.Add(name);

        detector.Start();
        Thread.Sleep(300);

        Assert.Empty(started);
    }

    // The subscriber the app installs stops a recording, which blocks for seconds. Raising the
    // events under the watcher's own lock puts Dispose — and every subsequent tick — behind that
    // handler; the events have to be raised with the lock released.
    [Fact]
    public void Dispose_DoesNotBlockBehindASlowSubscriber()
    {
        var ownName = Path.GetFileNameWithoutExtension(Environment.ProcessPath!);
        using var handlerEntered = new ManualResetEventSlim();
        using var releaseHandler = new ManualResetEventSlim();
        using var handlerExited = new ManualResetEventSlim();

        var detector = new ProcessNameGameDetector(new[] { ownName }, pollInterval: TimeSpan.FromMilliseconds(10));
        detector.GameStarted += _ =>
        {
            handlerEntered.Set();
            releaseHandler.Wait(TimeSpan.FromSeconds(10));
            handlerExited.Set();
        };

        try
        {
            detector.Start();
            Assert.True(handlerEntered.Wait(TimeSpan.FromSeconds(5)), "the start handler never ran");

            var started = Stopwatch.StartNew();
            detector.Dispose();
            started.Stop();

            Assert.True(started.Elapsed < TimeSpan.FromSeconds(2), $"Dispose took {started.Elapsed}");
        }
        finally
        {
            releaseHandler.Set();

            // Dispose deliberately does not wait for an in-flight handler, so the handler can still
            // be inside these events after this method's `using` scope would dispose them. Waiting
            // it out is the difference between a green test and an ObjectDisposedException thrown
            // on a timer thread — which took the whole test host with it on CI.
            Assert.True(handlerExited.Wait(TimeSpan.FromSeconds(5)), "the handler never finished");
        }
    }

    [Fact]
    public void CatalogueEntries_AreMatchedCaseInsensitively_AndWithAndWithoutExtension()
    {
        var ownName = Path.GetFileNameWithoutExtension(Environment.ProcessPath!);
        var started = new List<string>();
        using var detector = new ProcessNameGameDetector(
            new[] { ownName.ToUpperInvariant() + ".EXE", ownName.ToUpperInvariant() },
            pollInterval: TimeSpan.FromMilliseconds(10));
        detector.GameStarted += name => started.Add(name);

        detector.Start();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (started.Count == 0 && DateTime.UtcNow < deadline)
            Thread.Sleep(10);

        Assert.NotEmpty(started);
    }
}
