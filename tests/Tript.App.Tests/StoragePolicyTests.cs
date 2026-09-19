// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Reflection;
using Tript.App.Content;
using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

public sealed class StoragePolicyTests : IDisposable
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    private readonly string _root;
    private readonly AppHost _host;
    private readonly FakeStorageProbe _probe = new();

    public StoragePolicyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tript-storage-policy", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var settingsPath = Path.Combine(_root, "settings.json");
        var settings = new SettingsStore(new SettingsFileProvider(settingsPath));
        settings.Load().Storage.MinimumFreeBytes = 20 * Gigabyte;
        settings.Save();

        _host = new AppHost(new AppOptions
        {
            ContentRoot = _root,
            SettingsPath = settingsPath,
            WebRoot = _root,
            FakeRecorder = true,
        }, settings, runtime: null, new RecordingSessionTracker(),
            recorderStopTimeout: TimeSpan.FromMilliseconds(20));

        typeof(AppHost).GetField("_storageProbe", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_host, _probe);
    }

    public void Dispose()
    {
        _host.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Tick() => typeof(AppHost)
        .GetMethod("SampleStorage", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(_host, null);

    [Fact]
    public void BelowTheFloor_ARecordingIsRefusedBeforeAnyFileIsMade()
    {
        _probe.Free = 1 * Gigabyte;

        Assert.False(_host.StartRecording(null));
        Assert.False(_host.IsRecording);
        Assert.False(Directory.Exists(Path.Combine(_root, ContentLayout.Sessions)));
    }

    [Fact]
    public void AboveTheFloor_ARecordingStartsAsUsual()
    {
        _probe.Free = 400 * Gigabyte;

        Assert.True(_host.StartRecording(null));
        Assert.True(_host.IsRecording);
        _host.StopRecording();
    }

    [Fact]
    public void ReachingTheFloor_BlocksRecordingAndSaysWhy()
    {
        _probe.Free = 1 * Gigabyte;

        Tick();

        Assert.True(_host.RecordingBlockedByStorage);
        Assert.NotNull(_host.StorageBlockedReason());
        Assert.True(_host.BuildStreamerStatus().RecordingBlocked);
    }

    [Fact]
    public void RoomComingBack_LiftsTheBlockOnItsOwn()
    {
        _probe.Free = 1 * Gigabyte;
        Tick();
        Assert.True(_host.RecordingBlockedByStorage);

        _probe.Free = 400 * Gigabyte;
        Tick();

        Assert.False(_host.RecordingBlockedByStorage);
        Assert.Null(_host.StorageBlockedReason());
        Assert.False(_host.BuildStreamerStatus().RecordingBlocked);
    }

    [Fact]
    public void JustAboveTheFloorButBelowTheResumeLine_StaysBlocked()
    {
        _probe.Free = 1 * Gigabyte;
        Tick();

        _probe.Free = 21 * Gigabyte;
        Tick();

        Assert.True(_host.RecordingBlockedByStorage);
    }

    [Fact]
    public void BlockingStopsARecordingThatIsAlreadyRunning()
    {
        _probe.Free = 400 * Gigabyte;
        Assert.True(_host.StartRecording(null));

        _probe.Free = 1 * Gigabyte;
        Tick();

        Assert.True(_host.RecordingBlockedByStorage);
        Assert.False(_host.IsRecording);
    }

    [Fact]
    public void WithTheReclaimPolicy_TheOldestSessionIsRemovedWhenTheFloorIsReached()
    {
        UseReclaimPolicy();
        var old = WriteSession("session-20260101-000000000.mp4");

        _probe.Free = 1 * Gigabyte;
        Tick();

        Assert.False(File.Exists(old));
    }

    [Fact]
    public void WithTheReclaimPolicy_AFavouriteSurvivesEvenAtTheFloor()
    {
        UseReclaimPolicy();
        var favourite = WriteSession("session-20260101-000000000.mp4");
        _host.ToggleFavorite(new ToggleFavoriteParameters
        {
            ContentType = "recording",
            FilePath = Path.GetRelativePath(_root, favourite).Replace(Path.DirectorySeparatorChar, '/'),
            Favorite = true,
        });

        _probe.Free = 1 * Gigabyte;
        Tick();

        Assert.True(File.Exists(favourite));
    }

    [Fact]
    public void WithTheReclaimPolicy_WhenThereIsStillNotEnoughRoom_RecordingIsBlocked()
    {
        UseReclaimPolicy();
        WriteSession("session-20260101-000000000.mp4");

        _probe.Free = 1 * Gigabyte;
        Tick();

        Assert.True(_host.RecordingBlockedByStorage);
    }

    private void UseReclaimPolicy()
    {
        var settings = _host.SettingsStore;
        settings.Load().Storage.WhenFull = StorageFullAction.ReclaimOldest;
        settings.Save();
        typeof(AppHost).GetMethod("ApplyStorageSettings", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_host, [settings.Load()]);
    }

    private string WriteSession(string fileName)
    {
        var directory = Path.Combine(_root, "Overwatch", ContentLayout.Sessions);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, new byte[4 * 1024 * 1024]);
        return path;
    }

    [Fact]
    public void TheStatusNamesTheRecordingDrive_NotATighterScratchDrive()
    {
        _probe.ScratchRoot = @"C:\";
        _probe.ScratchFree = 1 * Gigabyte;
        _probe.Free = 400 * Gigabyte;

        Tick();

        var status = _host.BuildStorageStatus();
        Assert.Equal(@"T:\", status.VolumeRoot);
        Assert.Equal("ok", status.Pressure);
        Assert.False(_host.RecordingBlockedByStorage);
        Assert.Equal(@"C:\", status.ScratchRoot);
        Assert.True(status.ScratchLow);
    }

    [Fact]
    public void AFullScratchDriveOnAnotherVolume_DoesNotReclaimFromTheRecordingDrive()
    {
        UseReclaimPolicy();
        var session = WriteSession("session-20260101-000000000.mp4");
        _probe.ScratchRoot = @"C:\";
        _probe.ScratchFree = 0;
        _probe.Free = 400 * Gigabyte;

        Tick();

        Assert.True(File.Exists(session));
        Assert.False(_host.RecordingBlockedByStorage);
    }

    [Fact]
    public void TheScratchFolderIsMeasuredUnderTheRecordingRoot()
    {
        Tick();

        Assert.Contains(_probe.Measured, path =>
            path.StartsWith(_root, StringComparison.OrdinalIgnoreCase)
            && path.Contains(ContentLayout.Scratch, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheStatusCarriesTheFloorAndTheWarnLine()
    {
        _probe.Free = 400 * Gigabyte;
        Tick();

        var status = _host.BuildStorageStatus();
        Assert.Equal(20 * Gigabyte, status.MinimumFreeBytes);
        Assert.Equal(60 * Gigabyte, status.WarnFreeBytes);
        Assert.Equal("ok", status.Pressure);
        Assert.Equal("PauseRecording", status.WhenFull);
    }

    [Fact]
    public void AnUnreadableDrive_DoesNotBlockRecording()
    {
        _probe.Readable = false;

        Tick();

        Assert.False(_host.RecordingBlockedByStorage);
        Assert.Equal("unknown", _host.BuildStorageStatus().Pressure);
        Assert.True(_host.StartRecording(null));
        _host.StopRecording();
    }

    private sealed class FakeStorageProbe : IStorageProbe
    {
        internal long Free { get; set; } = 400 * Gigabyte;

        internal bool Readable { get; set; } = true;

        internal string? ScratchRoot { get; set; }

        internal long ScratchFree { get; set; } = 400 * Gigabyte;

        internal List<string> Measured { get; } = [];

        public VolumeSpace? Measure(string path)
        {
            Measured.Add(path);
            if (!Readable)
                return null;

            return ScratchRoot is { } scratch && IsScratch(path)
                ? new VolumeSpace(scratch, ScratchFree, 500 * Gigabyte)
                : new VolumeSpace(@"T:\", Free, 500 * Gigabyte);
        }

        private static bool IsScratch(string path) =>
            path.Contains(ContentLayout.Scratch, StringComparison.OrdinalIgnoreCase);
    }
}
