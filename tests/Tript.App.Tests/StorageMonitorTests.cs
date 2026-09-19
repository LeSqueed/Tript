// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.App.Content;
using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

public class StorageMonitorTests
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    private static StorageMonitor Monitor(long floorGigabytes = 20,
        StorageFullAction whenFull = StorageFullAction.PauseRecording, bool policyConfirmed = true)
    {
        var monitor = new StorageMonitor();
        monitor.Configure(new StorageSettings
        {
            MinimumFreeBytes = floorGigabytes * Gigabyte,
            WhenFull = whenFull,
            PolicyConfirmed = policyConfirmed,
        });
        return monitor;
    }

    private static VolumeSpace Volume(double freeGigabytes, string root = @"C:\", double totalGigabytes = 500) =>
        new(root, (long)(freeGigabytes * Gigabyte), (long)(totalGigabytes * Gigabyte));

    [Fact]
    public void WithNoReadableVolume_ThePressureIsUnknown()
    {
        var monitor = Monitor();
        monitor.Sample(null, null);

        Assert.Equal(StoragePressure.Unknown, monitor.Status.Pressure);
        Assert.Null(monitor.Status.VolumeRoot);
    }

    [Fact]
    public void WellAboveTheWarnLine_ThePressureIsOk()
    {
        var monitor = Monitor();
        monitor.Sample(Volume(200), null);

        Assert.Equal(StoragePressure.Ok, monitor.Status.Pressure);
    }

    [Fact]
    public void AtThreeTimesTheFloor_ThePressureIsWarning()
    {
        var monitor = Monitor();
        monitor.Sample(Volume(60), null);

        Assert.Equal(StoragePressure.Warning, monitor.Status.Pressure);
    }

    [Fact]
    public void AtTheFloor_ThePressureIsCritical()
    {
        var monitor = Monitor();
        monitor.Sample(Volume(20), null);

        Assert.Equal(StoragePressure.Critical, monitor.Status.Pressure);
    }

    [Fact]
    public void OnceBlocked_ItStaysCriticalUntilTheResumeThreshold()
    {
        var monitor = Monitor();
        monitor.Sample(Volume(20), null);
        monitor.SetRecordingBlocked(true);

        monitor.Sample(Volume(24), null);
        Assert.Equal(StoragePressure.Critical, monitor.Status.Pressure);

        monitor.Sample(Volume(26), null);
        Assert.Equal(StoragePressure.Warning, monitor.Status.Pressure);
    }

    [Fact]
    public void TheRecordingVolume_DecidesThePressure_EvenWhenTheScratchDriveIsTighter()
    {
        var monitor = Monitor();
        monitor.Sample(Volume(400, @"G:\"), Volume(5, @"C:\"));

        Assert.Equal(StoragePressure.Ok, monitor.Status.Pressure);
        Assert.Equal(@"G:\", monitor.Status.VolumeRoot);
        Assert.Equal(400 * Gigabyte, monitor.Status.FreeBytes);
    }

    [Fact]
    public void ATighterScratchDrive_IsReportedOnItsOwn()
    {
        var monitor = Monitor();
        monitor.Sample(Volume(400, @"G:\"), Volume(5, @"C:\"));

        Assert.Equal(@"C:\", monitor.Status.ScratchRoot);
        Assert.Equal(5 * Gigabyte, monitor.Status.ScratchFreeBytes);
        Assert.True(monitor.Status.ScratchLow);
    }

    [Fact]
    public void WhenBothVolumesAreTheSameDrive_ItIsCountedOnce()
    {
        var monitor = Monitor();
        monitor.Sample(Volume(200, @"C:\"), Volume(200, @"c:\"));

        Assert.Equal(StoragePressure.Ok, monitor.Status.Pressure);
        Assert.Equal(@"C:\", monitor.Status.VolumeRoot);
        Assert.Null(monitor.Status.ScratchRoot);
        Assert.False(monitor.Status.ScratchLow);
    }

    [Fact]
    public void AnUnreadableRecordingVolume_IsUnknown_EvenWithAReadableScratchDrive()
    {
        var monitor = Monitor();
        monitor.Sample(null, Volume(400, @"C:\"));

        Assert.Equal(StoragePressure.Unknown, monitor.Status.Pressure);
        Assert.Null(monitor.Status.VolumeRoot);
    }

    [Fact]
    public void HasRoomToRecord_UsesTheResumeFloorWhileBlocked()
    {
        var monitor = Monitor();
        monitor.Sample(Volume(20), null);
        monitor.SetRecordingBlocked(true);

        Assert.False(monitor.HasRoomToRecord(Volume(24), null, usesReplayBuffer: false));
        Assert.True(monitor.HasRoomToRecord(Volume(26), null, usesReplayBuffer: false));
    }

    [Fact]
    public void HasRoomToRecord_RefusesWhenTheScratchVolumeCannotHoldOneReplay()
    {
        var monitor = Monitor();
        monitor.SetReplayReserve(5 * Gigabyte);

        Assert.False(monitor.HasRoomToRecord(Volume(400, @"G:\"), Volume(1, @"C:\"),
            usesReplayBuffer: true));
    }

    [Fact]
    public void HasRoomToRecord_IgnoresTheScratchVolume_WhenTheReplayBufferIsOff()
    {
        var monitor = Monitor();
        monitor.SetReplayReserve(5 * Gigabyte);

        Assert.True(monitor.HasRoomToRecord(Volume(400, @"G:\"), Volume(1, @"C:\"),
            usesReplayBuffer: false));
    }

    [Fact]
    public void HasRoomToRecord_DoesNotHoldTheRecordingDriveToTheReplayReserveTwice()
    {
        var monitor = Monitor();
        monitor.SetReplayReserve(5 * Gigabyte);

        Assert.True(monitor.HasRoomToRecord(Volume(30, @"G:\"), Volume(30, @"g:\"),
            usesReplayBuffer: true));
    }

    [Fact]
    public void SmallChangesInFreeSpace_DoNotRaiseTheStatus()
    {
        var monitor = Monitor();
        var raised = 0;
        monitor.StatusChanged += _ => raised++;

        var free = 200 * Gigabyte + 10L * 1024 * 1024;
        monitor.Sample(new VolumeSpace(@"C:\", free, 500 * Gigabyte), null);
        var afterFirst = raised;

        monitor.Sample(new VolumeSpace(@"C:\", free - 1024, 500 * Gigabyte), null);

        Assert.Equal(afterFirst, raised);
    }

    [Fact]
    public void ConfiguringAHigherFloor_MovesThePressureUp()
    {
        var monitor = Monitor();
        monitor.Sample(Volume(100), null);
        Assert.Equal(StoragePressure.Ok, monitor.Status.Pressure);

        monitor.Configure(new StorageSettings { MinimumFreeBytes = 120 * Gigabyte, PolicyConfirmed = true });

        Assert.Equal(StoragePressure.Critical, monitor.Status.Pressure);
    }

    [Fact]
    public void ThePolicyAndItsConfirmation_ReachTheStatus()
    {
        var monitor = Monitor(whenFull: StorageFullAction.ReclaimOldest, policyConfirmed: false);
        monitor.Sample(Volume(200), null);

        Assert.Equal(StorageFullAction.ReclaimOldest, monitor.Status.WhenFull);
        Assert.False(monitor.Status.PolicyConfirmed);
    }
}
