// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Globalization;
using System.Text.Json;
using Serilog;
using Tript.App.Content;
using Tript.Obs;
using Tript.Recorder;
using Tript.Settings;
using SettingsModel = Tript.Settings.Settings;

namespace Tript.App;

internal sealed partial class AppHost
{
    private const long ReplayReserveHeadroomBytes = 1024L * 1024 * 1024;

    private static readonly TimeSpan StorageIdleInterval = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan StorageRecordingInterval = TimeSpan.FromSeconds(15);

    private readonly StorageMonitor _storageMonitor = new();

    private IStorageProbe _storageProbe = new DriveInfoStorageProbe();

    private Timer? _storageTimer;

    private CoalescingRunner? _storageReportPush;

    private bool _recordingBlockedByStorage;

    private int _reclaimRunning;

    private void InitializeStorage()
    {
        _storageReportPush = new CoalescingRunner(BroadcastStorageReport, ReportStorageFailure);
        _storageMonitor.StatusChanged += _ => PushStorageStatus();
        _storageMonitor.Configure(_settingsStore.Load().Storage);
        SampleStorage();
    }

    private void StartStorageWatch()
    {
        _storageTimer = new Timer(_ => SampleStorage(), null, StorageIdleInterval, StorageIdleInterval);
    }

    private void DisposeStorage() => _storageTimer?.Dispose();

    private void ApplyStorageSettings(SettingsModel settings)
    {
        _storageMonitor.Configure(settings.Storage);
        SampleStorage();
        PushStorageReport();
    }

    internal static string? ValidateStorageSettings(StorageSettings storage)
    {
        if (storage.MinimumFreeBytes < StorageSettings.LowestMinimumFreeBytes
            || storage.MinimumFreeBytes > StorageSettings.HighestMinimumFreeBytes)
        {
            return "the reserved free space must be between 1 GB and 2 TB.";
        }

        return null;
    }

    private void SetStorageWatchInterval(bool recording)
    {
        var period = recording ? StorageRecordingInterval : StorageIdleInterval;
        try
        {
            _storageTimer?.Change(period, period);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private VolumeSpace? MeasureContentVolume() => _storageProbe.Measure(EffectiveRoot);

    private VolumeSpace? MeasureScratchVolume() => _storageProbe.Measure(ReplayScratchDirectory());

    private void SampleStorage()
    {
        if (_disposed)
            return;

        try
        {
            _storageMonitor.SetReplayReserve(ReplayReserveBytes());
            _storageMonitor.Sample(MeasureContentVolume(), MeasureScratchVolume());
            FinalizeRecordingStoppedOutsideTript();
            ApplyStoragePolicy();
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "AppHost: the free space check failed.");
        }
    }

    private void ApplyStoragePolicy()
    {
        var status = _storageMonitor.Status;

        if (status.Pressure != StoragePressure.Critical)
        {
            ReleaseStorageBlock();
            return;
        }

        if (_recordingBlockedByStorage)
            return;

        if (status.WhenFull == StorageFullAction.ReclaimOldest)
        {
            var reached = TryReclaim(_storageMonitor.ResumeFreeBytes, dryRun: false,
                out var freed, out var removed);

            if (removed > 0)
            {
                Log.Information("AppHost: reclaimed {Freed} bytes from {Removed} items to stay above the floor.",
                    freed, removed);
                PushError(
                    $"Storage was running out, so Tript removed {removed} older item(s) and freed "
                    + $"{FormatBytes(freed)}. Favourites were kept.");
            }

            if (reached)
            {
                _storageMonitor.Sample(MeasureContentVolume(), MeasureScratchVolume());
                if (_storageMonitor.Status.Pressure != StoragePressure.Critical)
                    return;
            }
        }

        BlockRecordingForStorage(status);
    }

    private void BlockRecordingForStorage(StorageStatus status)
    {
        _recordingBlockedByStorage = true;
        _storageMonitor.SetRecordingBlocked(true);

        var wasRecording = IsRecording;
        if (wasRecording)
            StopRecording();

        SetCaptureHold(CaptureHoldWanted());

        var folder = status.VolumeRoot ?? EffectiveRoot;
        PushError(wasRecording
            ? $"Recording stopped because {folder} has only {FormatBytes(status.FreeBytes)} free. "
              + "Free up space or lower the reserved free space in Settings, Storage."
            : $"Recording is on hold because {folder} has only {FormatBytes(status.FreeBytes)} free. "
              + "Free up space or lower the reserved free space in Settings, Storage.");

        PushState(IsRecording, CurrentGameId);
        PushStreamerStatus();
    }

    private void ReleaseStorageBlock()
    {
        if (!_recordingBlockedByStorage)
            return;

        _recordingBlockedByStorage = false;
        _storageMonitor.SetRecordingBlocked(false);
        SetCaptureHold(false);
        PushState(IsRecording, CurrentGameId);
        PushStreamerStatus();
    }

    internal bool RecordingBlockedByStorage => _recordingBlockedByStorage;

    private bool CaptureHoldWanted()
    {
        if (!_recordingBlockedByStorage)
            return false;

        var settings = _settingsStore.Load();
        return settings.Storage.KeepSharingWhenFull && settings.Streaming.ShareEnabled;
    }

    internal string? StorageBlockedReason()
    {
        if (!_recordingBlockedByStorage)
            return null;

        var status = _storageMonitor.Status;
        return status.ScratchLow && status.Pressure != StoragePressure.Critical
            ? $"Only {FormatBytes(status.ScratchFreeBytes)} free on {status.ScratchRoot}, where replays are saved."
            : $"Only {FormatBytes(status.FreeBytes)} free on {status.VolumeRoot ?? EffectiveRoot}.";
    }

    private bool HasRoomToStartRecording(RecordingMode mode)
    {
        var content = MeasureContentVolume();
        var scratch = MeasureScratchVolume();
        _storageMonitor.SetReplayReserve(ReplayReserveBytes());
        _storageMonitor.Sample(content, scratch);
        return _storageMonitor.HasRoomToRecord(content, scratch, mode.UsesReplayBuffer());
    }

    private long ReplayReserveBytes() =>
        _settingsStore.Load().Buffer.MaxSizeBytes + ReplayReserveHeadroomBytes;

    private void FinalizeRecordingStoppedOutsideTript()
    {
        ObsOutputStopCode? code;
        string? error;

        lock (_recorderGate)
        {
            if (_disposed || _shuttingDown || _stopFinalizationPending || _recorder is null)
                return;
            if (Volatile.Read(ref _recordingProcessOwner) is null && _activeOutputPath is null)
                return;

            var snapshot = _recorder.Snapshot;
            if (snapshot.State != RecorderState.Idle
                || snapshot.LastStopReason != RecorderStopReason.OutputFailure)
            {
                return;
            }

            code = snapshot.LastStopCode;
            error = snapshot.LastError;
            FinalizeStoppedRecordingLocked();
        }

        Log.Warning("AppHost: the recording output stopped on its own ({Code}): {Error}", code, error);
        PushError(code == ObsOutputStopCode.NoSpace
            ? $"Recording stopped because {EffectiveRoot} ran out of space. What was written so far was kept."
            : $"Recording stopped because the output failed ({error ?? code?.ToString() ?? "unknown"}).");
    }

    internal void PushStorageStatus()
    {
        if (_disposed)
            return;

        _ipc.Broadcast("storageStatus",
            JsonSerializer.SerializeToElement(BuildStorageStatus(), Wire.Options));
    }

    internal StorageStatusInfo BuildStorageStatus()
    {
        var status = _storageMonitor.Status;
        return new StorageStatusInfo
        {
            Pressure = WirePressure(status.Pressure),
            FreeBytes = status.FreeBytes,
            TotalBytes = status.TotalBytes,
            MinimumFreeBytes = status.MinimumFreeBytes,
            WarnFreeBytes = status.WarnFreeBytes,
            RecordingBlocked = status.RecordingBlocked,
            PolicyConfirmed = status.PolicyConfirmed,
            WhenFull = status.WhenFull.ToString(),
            KeepSharingWhenFull = status.KeepSharingWhenFull,
            VolumeRoot = status.VolumeRoot,
            Root = EffectiveRoot,
            ScratchRoot = status.ScratchRoot,
            ScratchFreeBytes = status.ScratchFreeBytes,
            ScratchLow = status.ScratchLow,
        };
    }

    internal void PushStorageReport() => _storageReportPush?.Run();

    private void BroadcastStorageReport() => BroadcastStorageReport(ListContent());

    internal void BroadcastStorageReport(IReadOnlyList<ContentItem> items)
    {
        if (_disposed)
            return;

        _ipc.Broadcast("storageReport",
            JsonSerializer.SerializeToElement(BuildStorageReport(items), Wire.Options));
    }

    private void ReportStorageFailure(Exception exception)
    {
        Log.Warning(exception, "AppHost: the storage report could not be built");
        PushError($"The storage report could not be built ({exception.Message}).");
    }

    internal StorageReport BuildStorageReport() => BuildStorageReport(ListContent());

    internal StorageReport BuildStorageReport(IReadOnlyList<ContentItem> items) =>
        StorageReportBuilder.Build(EffectiveRoot, items, TrashEntries(), MeasureContentVolume());

    internal void ReclaimStorage(ReclaimStorageParameters? parameters)
    {
        var dryRun = parameters?.DryRun == true;
        if (!TryReclaim(_storageMonitor.ResumeFreeBytes, dryRun, out var freed, out var removed))
        {
            PushError(removed == 0
                ? "There was nothing left to remove. Everything that is not a favourite is already gone."
                : $"Only {FormatBytes(freed)} could be freed from {removed} item(s), which is still not enough.");
        }
        else if (!dryRun)
        {
            PushError($"Freed {FormatBytes(freed)} by removing {removed} item(s). Favourites were kept.");
        }

        SampleStorage();
        PushStorageReport();
    }

    private bool TryReclaim(long targetFreeBytes, bool dryRun, out long freed, out int removed)
    {
        freed = 0;
        removed = 0;

        if (Interlocked.Exchange(ref _reclaimRunning, 1) == 1)
            return false;

        try
        {
            var start = MeasureContentVolume()?.FreeBytes ?? 0;
            var needed = targetFreeBytes - start;
            if (needed <= 0)
                return true;

            if (!dryRun)
                PurgeExpiredTrash();

            foreach (var entry in _trash.List().OrderBy(entry => entry.DeletedAt))
            {
                if (freed >= needed)
                    break;

                var bytes = entry.FileSizeBytes ?? 0;
                if (!dryRun && !_trash.Purge(entry.Id, out var failure))
                {
                    Log.Warning("could not purge {EntryId} while reclaiming space: {Failure}", entry.Id, failure);
                    continue;
                }

                freed += bytes;
                removed++;
            }

            var items = ListContent();
            var activeSessions = ActiveSessionPaths();

            foreach (var item in ReclaimOrder(items, activeSessions))
            {
                if (freed >= needed)
                    break;

                if (!dryRun)
                {
                    DeleteItems([new DeleteContentParameters
                    {
                        ContentType = item.ContentType,
                        FileName = item.FilePath,
                        Permanent = true,
                    }], permanent: true);
                }

                freed += item.FileSizeBytes;
                removed++;
            }

            if (!dryRun && removed > 0)
            {
                PushContent();
                PushTrash();
            }

            return freed >= needed;
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "AppHost: reclaiming space failed.");
            return false;
        }
        finally
        {
            Volatile.Write(ref _reclaimRunning, 0);
        }
    }

    private IEnumerable<ContentItem> ReclaimOrder(IReadOnlyList<ContentItem> items,
        IReadOnlySet<string> activeSessions)
    {
        var eligible = items.Where(item => CanReclaim(item, activeSessions)).ToList();

        foreach (var session in eligible
            .Where(item => ContentLayout.IsSessionPath(item.FilePath))
            .OrderBy(item => item.StartTime ?? 0))
        {
            yield return session;
        }

        foreach (var clip in eligible
            .Where(item => !ContentLayout.IsSessionPath(item.FilePath))
            .OrderBy(item => item.StartTime ?? 0))
        {
            yield return clip;
        }
    }

    private bool CanReclaim(ContentItem item, IReadOnlySet<string> activeSessions)
    {
        if (item.Favorite)
            return false;
        if (item.Recording == true || item.VideoMissing == true)
            return false;
        if (item.AutomaticClipsProcessing)
            return false;
        if (activeSessions.Contains(item.FilePath))
            return false;
        if (item.SourceSessionPath is { Length: > 0 } source && activeSessions.Contains(source))
            return false;

        return item.FileSizeBytes > 0;
    }

    private HashSet<string> ActiveSessionPaths()
    {
        var active = new HashSet<string>(ContentPathComparer);

        lock (_recorderGate)
        {
            if (_activeSessionPath is { Length: > 0 } session)
                active.Add(RelativeToRoot(session));
        }

        lock (_automaticClipGate)
        {
            if (_automaticClipJob is { } job && !string.IsNullOrEmpty(job.SourceSessionPath))
                active.Add(job.SourceSessionPath);
        }

        return active;
    }

    internal static string WirePressure(StoragePressure pressure) => pressure switch
    {
        StoragePressure.Unknown => "unknown",
        StoragePressure.Ok => "ok",
        StoragePressure.Warning => "warning",
        StoragePressure.Critical => "critical",
        _ => throw new ArgumentOutOfRangeException(nameof(pressure), pressure, null),
    };

    internal static string FormatBytes(long bytes)
    {
        if (bytes < 0)
            bytes = 0;

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"{value:0.#} {units[unit]}");
    }
}
