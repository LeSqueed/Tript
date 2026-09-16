// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.App.Tests;

[Collection(SingleInstanceCollection.Name)]
public sealed class SingleInstanceProtocolTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    [Fact]
    public void ExitMessage_RaisesExitAndNothingElse()
    {
        using var instance = Tript.Shell.SingleInstance.TryAcquire();
        Assert.NotNull(instance);

        using var exited = new ManualResetEventSlim(false);
        using var activated = new ManualResetEventSlim(false);
        instance.ExitRequested += () => exited.Set();
        instance.ActivationRequested += () => activated.Set();

        Assert.True(Tript.Shell.SingleInstance.Send(Tript.Shell.SingleInstance.ExitMessage));

        Assert.True(exited.Wait(Settle));
        Assert.False(activated.IsSet);
    }

    [Fact]
    public void ActivationMessage_RaisesActivationAndNothingElse()
    {
        using var instance = Tript.Shell.SingleInstance.TryAcquire();
        Assert.NotNull(instance);

        using var exited = new ManualResetEventSlim(false);
        using var activated = new ManualResetEventSlim(false);
        instance.ExitRequested += () => exited.Set();
        instance.ActivationRequested += () => activated.Set();

        Assert.True(Tript.Shell.SingleInstance.Send(Tript.Shell.SingleInstance.ActivationMessage));

        Assert.True(activated.Wait(Settle));
        Assert.False(exited.IsSet);
    }

    [Fact]
    public void UnknownMessage_RaisesNothing()
    {
        using var instance = Tript.Shell.SingleInstance.TryAcquire();
        Assert.NotNull(instance);

        using var raised = new ManualResetEventSlim(false);
        instance.ExitRequested += () => raised.Set();
        instance.ActivationRequested += () => raised.Set();

        Assert.True(Tript.Shell.SingleInstance.Send("nonsense"));

        Assert.False(raised.Wait(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void ExitArrivingBeforeAnyoneSubscribes_IsReplayedOnSubscribe()
    {
        using var instance = Tript.Shell.SingleInstance.TryAcquire();
        Assert.NotNull(instance);

        Assert.True(Tript.Shell.SingleInstance.Send(Tript.Shell.SingleInstance.ExitMessage));
        Thread.Sleep(500);

        var raised = 0;
        instance.ExitRequested += () => Interlocked.Increment(ref raised);
        Assert.Equal(1, Volatile.Read(ref raised));

        instance.ExitRequested += () => Interlocked.Increment(ref raised);
        Assert.Equal(1, Volatile.Read(ref raised));
    }

    [Fact]
    public void RequestExit_WithNoInstanceRunning_SucceedsWithoutWaiting()
    {
        var started = DateTime.UtcNow;

        Assert.True(Tript.Shell.SingleInstance.RequestExit(TimeSpan.FromSeconds(30)));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }
}
