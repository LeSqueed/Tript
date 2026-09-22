// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.App.Content;
using Tript.Recorder;
using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

public sealed class TrayStatusTests
{
    private static TrayStatus Status(
        bool recording = false,
        RecordingMode? mode = null,
        string? gameName = null,
        bool blocked = false,
        StoragePressure pressure = StoragePressure.Ok,
        string? storageReason = null,
        StreamShareState share = StreamShareState.Off,
        bool shareEnabled = false) =>
        TrayStatus.From(recording, mode, gameName, blocked, pressure, storageReason, share, shareEnabled);

    [Fact]
    public void ReplayBufferOnly_ReadsAsBuffering_NotRecording()
    {
        Assert.Equal(TrayActivity.Buffering,
            Status(recording: true, mode: RecordingMode.ReplayBufferOnly).Activity);
    }

    [Theory]
    [InlineData(RecordingMode.Session)]
    [InlineData(RecordingMode.SessionWithReplayBuffer)]
    [InlineData(RecordingMode.Hybrid)]
    public void EverySessionMode_ReadsAsRecording(RecordingMode mode)
    {
        Assert.Equal(TrayActivity.Recording, Status(recording: true, mode: mode).Activity);
    }

    [Fact]
    public void AGameOnScreenWithoutRecording_ReadsAsDetected()
    {
        Assert.Equal(TrayActivity.Detected, Status(gameName: "Valorant").Activity);
    }

    [Fact]
    public void NothingHappening_ReadsAsIdle()
    {
        Assert.Equal(TrayActivity.Idle, Status().Activity);
        Assert.Equal(TrayAlert.None, Status().Alert);
    }

    [Fact]
    public void BlockedRecording_RaisesAnError()
    {
        Assert.Equal(TrayAlert.Error, Status(blocked: true).Alert);
    }

    [Fact]
    public void CriticalPressure_RaisesAnError()
    {
        Assert.Equal(TrayAlert.Error, Status(pressure: StoragePressure.Critical).Alert);
    }

    [Fact]
    public void LowSpace_RaisesAWarning()
    {
        Assert.Equal(TrayAlert.Warning, Status(pressure: StoragePressure.Warning).Alert);
    }

    [Fact]
    public void CriticalPressure_OutranksLowSpace()
    {
        var status = Status(blocked: true, pressure: StoragePressure.Warning, storageReason: "out of room");
        Assert.Equal(TrayAlert.Error, status.Alert);
        Assert.Equal("out of room", status.AlertReason);
    }

    [Theory]
    [InlineData(StreamShareState.Failed)]
    [InlineData(StreamShareState.Unsupported)]
    public void ABrokenShare_RaisesAWarning_WhenSharingIsOn(StreamShareState state)
    {
        Assert.Equal(TrayAlert.Warning, Status(share: state, shareEnabled: true).Alert);
    }

    [Theory]
    [InlineData(StreamShareState.Failed)]
    [InlineData(StreamShareState.Unsupported)]
    public void ABrokenShare_IsSilent_WhenSharingIsOff(StreamShareState state)
    {
        Assert.Equal(TrayAlert.None, Status(share: state).Alert);
    }

    [Fact]
    public void AnAlertNeverHidesWhatTheRecorderIsDoing()
    {
        var status = Status(recording: true, mode: RecordingMode.ReplayBufferOnly,
            pressure: StoragePressure.Warning);
        Assert.Equal(TrayActivity.Buffering, status.Activity);
        Assert.Equal(TrayAlert.Warning, status.Alert);
    }

    [Fact]
    public void TheTooltipNamesTheGameAndTheProblem()
    {
        var status = Status(recording: true, mode: RecordingMode.Session, gameName: "Valorant",
            pressure: StoragePressure.Warning, storageReason: "Space is running low on D:\\.");
        Assert.Equal("Tript - Recording: Valorant\nSpace is running low on D:\\.", status.Tooltip());
    }

    [Fact]
    public void TheIdleTooltipIsJustTheName()
    {
        Assert.Equal("Tript", Status().Tooltip());
    }

    // szTip is a ByValTStr of 128, and marshalling a longer string throws at the P/Invoke boundary.
    [Fact]
    public void ALongGameNameAndReasonStayWithinTheTooltipLimit()
    {
        var status = Status(recording: true, mode: RecordingMode.Session,
            gameName: new string('g', 400), pressure: StoragePressure.Warning,
            storageReason: new string('r', 400));
        Assert.True(status.Tooltip().Length <= TrayStatus.TooltipLimit,
            $"tooltip was {status.Tooltip().Length} characters");
    }
}
