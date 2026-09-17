// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.ComponentModel;
using Xunit;

namespace Tript.App.Tests;

public sealed class ObsProcessWatcherTests
{
    [Fact]
    public void Polling_ReportsOnlyChanges()
    {
        var presence = ObsPresence.Absent;
        var reported = new List<ObsPresence>();
        using var watcher = new ObsProcessWatcher(reported.Add, () => presence, TimeSpan.FromHours(1));

        watcher.Poll();
        presence = new ObsPresence(true, "32.0.1");
        watcher.Poll();
        watcher.Poll();
        presence = ObsPresence.Absent;
        watcher.Poll();

        Assert.Equal([new ObsPresence(true, "32.0.1"), ObsPresence.Absent], reported);
        Assert.Equal(ObsPresence.Absent, watcher.Current);
    }

    [Fact]
    public void AProbeThatCannotListProcesses_KeepsTheLastAnswer()
    {
        var fail = false;
        var reported = new List<ObsPresence>();
        using var watcher = new ObsProcessWatcher(reported.Add,
            () => fail ? throw new Win32Exception(5) : new ObsPresence(true, null), TimeSpan.FromHours(1));

        watcher.Poll();
        fail = true;
        watcher.Poll();

        Assert.Single(reported);
        Assert.True(watcher.Current.Running);
    }

    [Fact]
    public void Watching_PollsStraightAwayAndStopsWhenAsked()
    {
        using var seen = new ManualResetEventSlim();
        using var watcher = new ObsProcessWatcher(_ => seen.Set(), () => new ObsPresence(true, null),
            TimeSpan.FromHours(1));

        Assert.False(watcher.IsWatching);
        watcher.SetWatching(true);

        Assert.True(watcher.IsWatching);
        Assert.True(seen.Wait(TimeSpan.FromSeconds(5)));

        watcher.SetWatching(false);
        Assert.False(watcher.IsWatching);
    }

    [Fact]
    public void WatchingAgain_ReportsAnObsThatStayedOpen()
    {
        var reports = 0;
        using var reported = new SemaphoreSlim(0);
        using var watcher = new ObsProcessWatcher(_ =>
        {
            Interlocked.Increment(ref reports);
            reported.Release();
        }, () => new ObsPresence(true, null), TimeSpan.FromHours(1));

        watcher.SetWatching(true);
        Assert.True(reported.Wait(TimeSpan.FromSeconds(5)));
        watcher.SetWatching(false);
        Assert.False(watcher.Current.Running);

        watcher.SetWatching(true);
        Assert.True(reported.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, reports);
    }

    [Fact]
    public void ADisposedWatcher_NeitherWatchesNorReports()
    {
        var reported = new List<ObsPresence>();
        var watcher = new ObsProcessWatcher(reported.Add, () => new ObsPresence(true, null), TimeSpan.FromHours(1));
        watcher.Dispose();

        watcher.SetWatching(true);
        watcher.Poll();

        Assert.False(watcher.IsWatching);
        Assert.Empty(reported);
    }

    [Fact]
    public void TheRealProbe_AnswersWithoutThrowing()
    {
        var presence = ObsProcessWatcher.Probe();
        Assert.True(presence.Running || presence.Version is null);
    }
}
