// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Serilog;
using Tript.Recorder;
using Tript.Settings;

namespace Tript.App;

internal sealed class AudioLevelFeed : IDisposable
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LevelInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DeviceInterval = TimeSpan.FromSeconds(5);

    private readonly SettingsStore _settingsStore;
    private readonly ObsAudioLevelMonitor? _monitor;
    private readonly AudioDeviceInventory _devices;
    private readonly Action<IReadOnlyDictionary<string, float>> _publishLevels;
    private readonly Action _devicesChanged;
    private readonly TimeProvider _time;
    private readonly Func<bool> _deferDeviceRefresh;
    private readonly Lock _levelGate = new();
    private readonly Lock _deviceGate = new();
    private Timer? _levelTimer;
    private Timer? _deviceTimer;
    private long _wantedUntilTicks;
    private string? _lastFailure;
    private bool _disposed;

    internal AudioLevelFeed(SettingsStore settingsStore, ObsAudioLevelMonitor? monitor,
        AudioDeviceInventory devices, Action<IReadOnlyDictionary<string, float>> publishLevels,
        Action devicesChanged, TimeProvider? time = null, Func<bool>? deferDeviceRefresh = null)
    {
        _settingsStore = settingsStore;
        _monitor = monitor;
        _devices = devices;
        _publishLevels = publishLevels;
        _devicesChanged = devicesChanged;
        _time = time ?? TimeProvider.System;
        _deferDeviceRefresh = deferDeviceRefresh ?? (() => false);
    }

    internal IReadOnlyList<AudioDeviceSetting> Devices => _devices.Snapshot;

    internal bool Wanted => _time.GetUtcNow().UtcTicks <= Interlocked.Read(ref _wantedUntilTicks);

    internal void Watch() =>
        Interlocked.Exchange(ref _wantedUntilTicks, (_time.GetUtcNow() + Lease).UtcTicks);

    internal void LoadDevices() => _devices.Refresh();

    internal void Start()
    {
        if (_monitor is not null)
            _levelTimer = new Timer(_ => PublishLevels(), null, TimeSpan.Zero, LevelInterval);
        _deviceTimer = new Timer(_ => RefreshDevices(), null, DeviceInterval, DeviceInterval);
    }

    internal void PublishLevels()
    {
        lock (_levelGate)
        {
            if (_disposed || _monitor is null)
                return;

            try
            {
                if (!Wanted)
                {
                    _monitor.Read([]);
                    return;
                }

                var sources = _settingsStore.Load().Audio.Tracks
                    .SelectMany(track => track.Sources)
                    .Where(source => !string.IsNullOrWhiteSpace(source.DeviceId))
                    .Select(source => new AudioLevelSource(source.Kind, source.DeviceId!))
                    .Distinct()
                    .ToList();
                _publishLevels(_monitor.Read(sources));
            }
            catch (Exception exception)
            {
                if (_lastFailure != exception.Message)
                {
                    _lastFailure = exception.Message;
                    Log.Warning(exception, "AppHost: audio levels could not be read.");
                }
            }
        }
    }

    internal void RefreshDevices()
    {
        lock (_deviceGate)
        {
            if (_disposed || (_deferDeviceRefresh() && !Wanted) || !_devices.Refresh())
                return;
            _devicesChanged();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _levelTimer?.Dispose();
        lock (_levelGate)
        {
        }
        _deviceTimer?.Dispose();
        lock (_deviceGate)
        {
        }
        _monitor?.Dispose();
    }
}
