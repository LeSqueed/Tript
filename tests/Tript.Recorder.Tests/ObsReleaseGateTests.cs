// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;
using Xunit;

namespace Tript.Recorder.Tests;

// The gate that stops a handle releasing into a context that has been shut down. The ordering it
// enforces against obs_shutdown needs a live libobs context and is covered by the tier-2 tests;
// what is testable here without one is the refusal itself, which is the whole mechanism: a handle
// stamped with an older generation must never be let through.
public sealed class ObsReleaseGateTests
{
    [Fact]
    public void AHandleStampedWithTheCurrentGeneration_MayRelease()
    {
        Assert.True(ObsRuntime.TryEnterRelease(ObsRuntime.Generation));
        ObsRuntime.ExitRelease();
    }

    [Fact]
    public void AHandleStampedWithAnEarlierGeneration_IsRefused()
    {
        Assert.False(ObsRuntime.TryEnterRelease(ObsRuntime.Generation - 1));
    }

    // A refused entry must leave the in-flight count where it found it, or a later shutdown waits
    // out its drain budget for a release that is not happening.
    [Fact]
    public void ARefusedEntry_DoesNotLeaveTheGateHeld()
    {
        for (var i = 0; i < 100; i++)
            Assert.False(ObsRuntime.TryEnterRelease(ObsRuntime.Generation - 1));

        Assert.True(ObsRuntime.TryEnterRelease(ObsRuntime.Generation));
        ObsRuntime.ExitRelease();
    }
}
