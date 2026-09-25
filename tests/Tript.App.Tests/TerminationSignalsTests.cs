// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;
using Xunit;

namespace Tript.App.Tests;

public sealed class TerminationSignalsTests
{
    [Theory]
    [InlineData(PosixSignal.SIGTERM)]
    [InlineData(PosixSignal.SIGHUP)]
    [InlineData(PosixSignal.SIGINT)]
    public void ASignal_CancelsTheDefaultKillAndRunsTheGracefulExit(PosixSignal signal)
    {
        using var ran = new ManualResetEventSlim();
        using var signals = TerminationSignals.ForTesting(ran.Set);
        var context = new PosixSignalContext(signal);

        signals.Handle(context);

        Assert.True(context.Cancel);
        Assert.True(ran.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void RepeatedSignals_RunTheGracefulExitOnce()
    {
        var runs = 0;
        using var first = new ManualResetEventSlim();
        using var signals = TerminationSignals.ForTesting(() =>
        {
            Interlocked.Increment(ref runs);
            first.Set();
        });

        signals.Handle(new PosixSignalContext(PosixSignal.SIGTERM));
        var second = new PosixSignalContext(PosixSignal.SIGINT);
        signals.Handle(second);

        Assert.True(first.Wait(TimeSpan.FromSeconds(5)));
        Thread.Sleep(100);
        Assert.Equal(1, runs);
        Assert.True(second.Cancel);
    }

    [Fact]
    public void AGracefulExitThatThrows_DoesNotEscapeToThePool()
    {
        using var ran = new ManualResetEventSlim();
        using var signals = TerminationSignals.ForTesting(() =>
        {
            ran.Set();
            throw new InvalidOperationException("stop failed");
        });

        signals.Handle(new PosixSignalContext(PosixSignal.SIGTERM));

        Assert.True(ran.Wait(TimeSpan.FromSeconds(5)));
    }
}
