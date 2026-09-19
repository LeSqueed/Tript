// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;

namespace Tript.App.Content;

internal enum StoragePressure
{
    Unknown,
    Ok,
    Warning,
    Critical
}

internal sealed record StorageStatus(
    StoragePressure Pressure,
    long FreeBytes,
    long TotalBytes,
    long MinimumFreeBytes,
    long WarnFreeBytes,
    bool RecordingBlocked,
    bool PolicyConfirmed,
    StorageFullAction WhenFull,
    bool KeepSharingWhenFull,
    string? VolumeRoot,
    string? ScratchRoot,
    long ScratchFreeBytes,
    bool ScratchLow)
{
    internal static readonly StorageStatus Initial = new(StoragePressure.Unknown, 0, 0,
        StorageSettings.DefaultMinimumFreeBytes, StorageSettings.DefaultMinimumFreeBytes * WarnMultiplier,
        false, false, StorageFullAction.PauseRecording, true, null, null, 0, false);

    internal const int WarnMultiplier = 3;
}

internal sealed class StorageMonitor
{
    private const double ResumeMultiplier = 1.25;

    private const long ReportedByteStep = 128L * 1024 * 1024;

    private readonly Lock _gate = new();

    private long _minimumFreeBytes = StorageSettings.DefaultMinimumFreeBytes;
    private StorageFullAction _whenFull = StorageFullAction.PauseRecording;
    private bool _policyConfirmed;
    private bool _keepSharingWhenFull = true;

    private VolumeSpace? _content;
    private VolumeSpace? _scratch;
    private long _replayReserveBytes = StorageSettings.DefaultMinimumFreeBytes;
    private bool _recordingBlocked;

    private StorageStatus _status = StorageStatus.Initial;

    internal event Action<StorageStatus>? StatusChanged;

    internal StorageStatus Status
    {
        get
        {
            lock (_gate)
                return _status;
        }
    }

    internal bool RecordingBlocked
    {
        get
        {
            lock (_gate)
                return _recordingBlocked;
        }
    }

    internal void Configure(StorageSettings settings)
    {
        lock (_gate)
        {
            _minimumFreeBytes = Math.Clamp(settings.MinimumFreeBytes,
                StorageSettings.LowestMinimumFreeBytes, StorageSettings.HighestMinimumFreeBytes);
            _whenFull = settings.WhenFull;
            _policyConfirmed = settings.PolicyConfirmed;
            _keepSharingWhenFull = settings.KeepSharingWhenFull;
        }

        Reconcile();
    }

    internal void Sample(VolumeSpace? content, VolumeSpace? scratch)
    {
        lock (_gate)
        {
            _content = content;
            _scratch = scratch;
        }

        Reconcile();
    }

    internal void SetReplayReserve(long bytes)
    {
        lock (_gate)
        {
            if (_replayReserveBytes == bytes)
                return;

            _replayReserveBytes = bytes;
        }

        Reconcile();
    }

    internal void SetRecordingBlocked(bool blocked)
    {
        lock (_gate)
        {
            if (_recordingBlocked == blocked)
                return;

            _recordingBlocked = blocked;
        }

        Reconcile();
    }

    internal long MinimumFreeBytes
    {
        get
        {
            lock (_gate)
                return _minimumFreeBytes;
        }
    }

    internal long ResumeFreeBytes
    {
        get
        {
            lock (_gate)
                return ResumeFloor(_minimumFreeBytes);
        }
    }

    private static long ResumeFloor(long minimumFreeBytes) => (long)(minimumFreeBytes * ResumeMultiplier);

    internal bool HasRoomToRecord(VolumeSpace? content, VolumeSpace? scratch, bool usesReplayBuffer)
    {
        long floor;
        long replayReserve;
        lock (_gate)
        {
            floor = _recordingBlocked ? ResumeFloor(_minimumFreeBytes) : _minimumFreeBytes;
            replayReserve = _replayReserveBytes;
        }

        if (content is { } room && room.FreeBytes <= floor)
            return false;

        return !usesReplayBuffer || !IsSeparateScratch(content, scratch)
            || scratch is not { } saves || saves.FreeBytes > replayReserve;
    }

    private static bool IsSeparateScratch(VolumeSpace? content, VolumeSpace? scratch) =>
        content is { } room && scratch is { } saves
        && !string.Equals(room.RootPath, saves.RootPath, StringComparison.OrdinalIgnoreCase);

    private void Reconcile()
    {
        StorageStatus next;
        lock (_gate)
        {
            next = ComputeStatus();
            if (next == _status)
                return;

            _status = next;
        }

        StatusChanged?.Invoke(next);
    }

    private StorageStatus ComputeStatus()
    {
        var warn = _minimumFreeBytes * StorageStatus.WarnMultiplier;
        var separateScratch = IsSeparateScratch(_content, _scratch);
        var scratchRoot = separateScratch ? _scratch!.Value.RootPath : null;
        var scratchFree = separateScratch ? Reported(_scratch!.Value.FreeBytes) : 0;
        var scratchLow = separateScratch && _scratch!.Value.FreeBytes <= _replayReserveBytes;

        if (_content is not { } space)
        {
            return new StorageStatus(StoragePressure.Unknown, 0, 0, _minimumFreeBytes, warn,
                _recordingBlocked, _policyConfirmed, _whenFull, _keepSharingWhenFull, null,
                scratchRoot, scratchFree, scratchLow);
        }

        var critical = space.FreeBytes <= _minimumFreeBytes
            || (_recordingBlocked && space.FreeBytes < ResumeFloor(_minimumFreeBytes));

        var pressure = critical
            ? StoragePressure.Critical
            : space.FreeBytes <= warn
                ? StoragePressure.Warning
                : StoragePressure.Ok;

        return new StorageStatus(pressure, Reported(space.FreeBytes), Reported(space.TotalBytes),
            _minimumFreeBytes, warn, _recordingBlocked, _policyConfirmed, _whenFull, _keepSharingWhenFull,
            space.RootPath, scratchRoot, scratchFree, scratchLow);
    }

    private static long Reported(long bytes) => bytes / ReportedByteStep * ReportedByteStep;
}
