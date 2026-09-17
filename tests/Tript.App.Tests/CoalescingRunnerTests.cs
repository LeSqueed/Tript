// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.App.Tests;

public sealed class CoalescingRunnerTests
{
    [Fact]
    public void Run_WhenIdle_DoesTheWorkBeforeReturning()
    {
        var runs = 0;
        var runner = new CoalescingRunner(() => runs++, _ => { });

        runner.Run();

        Assert.Equal(1, runs);
    }

    [Fact]
    public void Run_WhileRunning_QueuesExactlyOneMoreRun()
    {
        var runs = 0;
        CoalescingRunner? runner = null;
        runner = new CoalescingRunner(() =>
        {
            runs++;
            if (runs == 1)
            {
                runner!.Run();
                runner.Run();
                runner.Run();
            }
        }, _ => { });

        runner.Run();

        Assert.Equal(2, runs);
    }

    [Fact]
    public void Run_AfterAFailure_ReportsItAndRunsAgainNextTime()
    {
        var runs = 0;
        var failures = new List<Exception>();
        var runner = new CoalescingRunner(() =>
        {
            runs++;
            if (runs == 1)
                throw new IOException("disk gone");
        }, failures.Add);

        runner.Run();
        runner.Run();

        Assert.Equal(2, runs);
        var failure = Assert.Single(failures);
        Assert.Equal("disk gone", failure.Message);
    }

    [Fact]
    public void Run_FromManyThreads_NeverOverlaps()
    {
        var active = 0;
        var overlapped = false;
        var runner = new CoalescingRunner(() =>
        {
            if (Interlocked.Increment(ref active) > 1)
                overlapped = true;
            Thread.Sleep(5);
            Interlocked.Decrement(ref active);
        }, _ => { });

        Parallel.For(0, 64, _ => runner.Run());

        Assert.False(overlapped);
    }
}
