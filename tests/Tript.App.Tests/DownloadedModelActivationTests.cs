// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.App.Tests;

public sealed class DownloadedModelActivationTests
{
    [Fact]
    public void Replacement_stops_matching_active_detection_before_model_invalidation()
    {
        var modelReferenced = true;
        var restarted = false;

        AppHost.ActivateDownloadedModelCore("manual-model", "manual-model",
            () => modelReferenced = false,
            () => Assert.False(modelReferenced, "The active detector still references the model."),
            gameId => restarted = gameId == "manual-model");

        Assert.True(restarted);
    }

    [Fact]
    public void Replacement_does_not_stop_detection_for_another_model()
    {
        var stopped = false;

        AppHost.ActivateDownloadedModelCore("downloaded", "manually-active",
            () => stopped = true, () => { }, _ => true);

        Assert.False(stopped);
    }

    [Fact]
    public void AModelArrivingWhileItsGameIsRecordedWithoutDetection_StartsDetection()
    {
        var started = new List<string>();

        var startedLate = AppHost.ActivateDownloadedModelCore("overwatch", activeDetectionGameId: null,
            () => throw new InvalidOperationException("Nothing was running to stop."), () => { },
            gameId =>
            {
                started.Add(gameId);
                return true;
            }, startWhenIdle: true);

        Assert.True(startedLate);
        Assert.Equal(["overwatch"], started);
    }

    [Fact]
    public void AModelArrivingWhenDetectionShouldStayIdle_StartsNothing()
    {
        var started = 0;

        var startedLate = AppHost.ActivateDownloadedModelCore("overwatch", activeDetectionGameId: null,
            () => { }, () => { }, _ => ++started > 0, startWhenIdle: false);

        Assert.False(startedLate);
        Assert.Equal(0, started);
    }

    [Fact]
    public void AReplacementForTheActiveDetection_IsRestartedOnceAndNotReportedAsALateStart()
    {
        var started = 0;

        var startedLate = AppHost.ActivateDownloadedModelCore("overwatch", "overwatch",
            () => { }, () => { }, _ => ++started > 0, startWhenIdle: true);

        Assert.False(startedLate);
        Assert.Equal(1, started);
    }

    [Fact]
    public void ALateStartThatTheDetectorRefuses_IsNotReportedAsStarted() =>
        Assert.False(AppHost.ActivateDownloadedModelCore("overwatch", null, () => { }, () => { }, _ => false,
            startWhenIdle: true));

    private static bool Decide(string gameId, bool recording = true, bool stopPending = false,
        string? active = null, bool owned = true, string? current = "overwatch",
        Func<string?, string?>? canonical = null) =>
        AppHost.ShouldStartDetectionForLateModel(gameId, recording, stopPending, active, owned, current,
            canonical ?? (_ => null));

    [Fact]
    public void ARecordingOfThisGameWithNoDetection_WantsTheLateModel() =>
        Assert.True(Decide("overwatch"));

    [Fact]
    public void TheGameIdIsComparedIgnoringCase() =>
        Assert.True(Decide("OVERWATCH"));

    [Fact]
    public void ARecordingOfAnotherGame_DoesNotWantTheModel() =>
        Assert.False(Decide("doom"));

    [Fact]
    public void AStoppingOrIdleRecorder_DoesNotWantTheModel()
    {
        Assert.False(Decide("overwatch", recording: false));
        Assert.False(Decide("overwatch", stopPending: true));
    }

    [Fact]
    public void ARecordingWithDetectionAlreadyRunning_LeavesItToTheReplacementPath() =>
        Assert.False(Decide("overwatch", active: "overwatch"));

    [Fact]
    public void AManualRecordingWithoutADetectedGameProcess_DoesNotStartDetection() =>
        Assert.False(Decide("overwatch", owned: false));

    [Fact]
    public void AModelDownloadedUnderAnOldGameIdStillMatchesTheRecordingsCurrentId()
    {
        Func<string?, string?> aliases = id => id == "old-overwatch-id" ? "overwatch" : null;

        Assert.True(Decide("old-overwatch-id", canonical: aliases));
    }
}
