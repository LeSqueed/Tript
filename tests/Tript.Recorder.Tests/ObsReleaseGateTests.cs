// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;
using Xunit;

namespace Tript.Recorder.Tests;

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

    [Fact]
    public void ARefusedEntry_DoesNotLeaveTheGateHeld()
    {
        for (var i = 0; i < 100; i++)
            Assert.False(ObsRuntime.TryEnterRelease(ObsRuntime.Generation - 1));

        Assert.True(ObsRuntime.TryEnterRelease(ObsRuntime.Generation));
        ObsRuntime.ExitRelease();
    }
}
