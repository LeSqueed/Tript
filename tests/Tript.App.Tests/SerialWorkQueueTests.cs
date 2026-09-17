// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Collections.Concurrent;
using Xunit;

namespace Tript.App.Tests;

public sealed class SerialWorkQueueTests
{
    [Fact]
    public void Items_RunOneAtATimeInOrder_AndTheQueueGoesIdle()
    {
        var order = new ConcurrentQueue<int>();
        var running = 0;
        var overlapped = false;
        using var release = new ManualResetEventSlim(false);
        var queue = new SerialWorkQueue<int>(item =>
        {
            if (Interlocked.Increment(ref running) > 1)
                overlapped = true;
            if (item == 1)
                release.Wait(TimeSpan.FromSeconds(5));
            order.Enqueue(item);
            Interlocked.Decrement(ref running);
        }, (_, _) => { });

        queue.Enqueue(1);
        queue.Enqueue(2);
        queue.Enqueue(3);
        Assert.True(queue.IsActive);
        release.Set();

        Assert.True(SpinWait.SpinUntil(() => !queue.IsActive, TimeSpan.FromSeconds(5)));
        Assert.Equal([1, 2, 3], order.ToArray());
        Assert.False(overlapped);
    }

    [Fact]
    public void AFailingItem_IsReported_AndLaterItemsStillRun()
    {
        var failures = new ConcurrentQueue<(int Item, string Message)>();
        var done = new ConcurrentQueue<int>();
        var queue = new SerialWorkQueue<int>(item =>
        {
            if (item == 1)
                throw new InvalidOperationException("broken");
            done.Enqueue(item);
        }, (item, exception) => failures.Enqueue((item, exception.Message)));

        queue.Enqueue(1);
        queue.Enqueue(2);

        Assert.True(SpinWait.SpinUntil(() => !queue.IsActive, TimeSpan.FromSeconds(5)));
        Assert.Equal([(1, "broken")], failures.ToArray());
        Assert.Equal([2], done.ToArray());
    }
}
