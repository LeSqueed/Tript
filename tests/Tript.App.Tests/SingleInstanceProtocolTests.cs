// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.App.Tests;

// The pipe protocol is the only route a deploy script has to a graceful shutdown, so the wire
// literals and the latch that replays a request arriving before anyone subscribed are worth
// pinning. Both ends run in this process, under the test host's own instance scope, so these
// never touch a real Tript.
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

    // An exit request can land during the fifteen seconds the shell spends waiting for its UI host,
    // which is before OpenWindow subscribes. Dropping it there would leave a deploy waiting out the
    // full timeout on a process that was told to quit.
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

    // Nothing is listening on the test host's scope, so this is the "asked a Tript that is not
    // running to quit" case: it must report success immediately rather than sitting out the wait.
    [Fact]
    public void RequestExit_WithNoInstanceRunning_SucceedsWithoutWaiting()
    {
        var started = DateTime.UtcNow;

        Assert.True(Tript.Shell.SingleInstance.RequestExit(TimeSpan.FromSeconds(30)));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }
}
