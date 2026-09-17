// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.App.Tests;

public sealed class RecordingDeferralTests
{
    [Fact]
    public void WhileIdle_WorkRunsImmediately()
    {
        var deferral = new RecordingDeferral(work => work());
        var runs = 0;

        deferral.Run("purge", () => runs++);

        Assert.Equal(1, runs);
    }

    [Fact]
    public void WhileRecording_WorkWaitsAndRunsOnceWhenTheRecordingEnds()
    {
        var deferral = new RecordingDeferral(work => work());
        var runs = 0;

        deferral.SetRecording(true);
        deferral.Run("purge", () => runs++);
        deferral.Run("purge", () => runs++);
        Assert.Equal(0, runs);

        deferral.SetRecording(true);
        Assert.Equal(0, runs);

        deferral.SetRecording(false);
        Assert.Equal(1, runs);

        deferral.SetRecording(false);
        Assert.Equal(1, runs);
    }

    [Fact]
    public void DifferentJobs_AreEachKept()
    {
        var deferral = new RecordingDeferral(work => work());
        var ran = new List<string>();

        deferral.SetRecording(true);
        deferral.Run("purge", () => ran.Add("purge"));
        deferral.Run("update", () => ran.Add("update"));
        deferral.SetRecording(false);

        Assert.Equal(["purge", "update"], ran.Order());
    }

    [Fact]
    public void AFailingJob_DoesNotStopTheOthers()
    {
        var deferral = new RecordingDeferral(work => work());
        var ran = false;

        deferral.SetRecording(true);
        deferral.Run("broken", () => throw new InvalidOperationException("boom"));
        deferral.Run("update", () => ran = true);
        deferral.SetRecording(false);

        Assert.True(ran);
    }
}
