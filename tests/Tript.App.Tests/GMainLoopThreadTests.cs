// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Shell.Linux;
using Xunit;

namespace Tript.App.Tests;

public sealed class GMainLoopThreadTests
{
    [SkippableFact]
    public void InvokedWork_RunsOnTheLoopThreadAndReturnsItsResult()
    {
        Skip.IfNot(GLibRoundTrip.Available, "GLib is not installed on this machine.");
        using var loop = new GMainLoopThread("Tript test loop");

        var (name, onLoop) = loop.Invoke(() => (Thread.CurrentThread.Name, loop.IsCurrent));

        Assert.Equal("Tript test loop", name);
        Assert.True(onLoop);
        Assert.False(loop.IsCurrent);
    }

    [SkippableFact]
    public void PostedWork_RunsInOrder()
    {
        Skip.IfNot(GLibRoundTrip.Available, "GLib is not installed on this machine.");
        using var loop = new GMainLoopThread("Tript test loop");
        var seen = new List<int>();

        for (var index = 0; index < 50; index++)
        {
            var value = index;
            Assert.True(loop.Post(() => seen.Add(value)));
        }

        loop.Invoke(() => true);
        Assert.Equal(Enumerable.Range(0, 50), seen);
    }

    [SkippableFact]
    public void AFailureInInvokedWork_ReachesTheCaller()
    {
        Skip.IfNot(GLibRoundTrip.Available, "GLib is not installed on this machine.");
        using var loop = new GMainLoopThread("Tript test loop");

        Assert.Throws<InvalidOperationException>(() => loop.Invoke<bool>(() => throw new InvalidOperationException()));
        Assert.True(loop.Invoke(() => true));
    }

    [SkippableFact]
    public void ADisposedLoop_RefusesNewWork()
    {
        Skip.IfNot(GLibRoundTrip.Available, "GLib is not installed on this machine.");
        var loop = new GMainLoopThread("Tript test loop");

        loop.Dispose();

        Assert.False(loop.Post(() => { }));
        Assert.Throws<DBusException>(() => loop.Invoke(() => true));
    }
}
