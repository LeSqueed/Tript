// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Recorder;
using Tript.Settings;

namespace Tript.App;

internal sealed class AudioDeviceInventory
{
    private readonly object _gate = new();
    private readonly Func<AudioSourceKind, (bool Success, IReadOnlyList<AudioDeviceSetting> Devices)> _enumerate;
    private AudioDeviceSetting[] _inputs = [];
    private AudioDeviceSetting[] _outputs = [];
    private AudioDeviceSetting[] _snapshot = [];

    internal AudioDeviceInventory(
        Func<AudioSourceKind, (bool Success, IReadOnlyList<AudioDeviceSetting> Devices)>? enumerate = null)
    {
        _enumerate = enumerate ?? EnumerateWasapi;
    }

    internal static AudioDeviceInventory ForPlatform(bool obsRunning) =>
        new(SourceFor(OperatingSystem.IsWindows(), obsRunning) switch
        {
            AudioDeviceSource.Wasapi => EnumerateWasapi,
            AudioDeviceSource.Obs => EnumerateObs,
            _ => _ => (true, []),
        });

    internal static AudioDeviceSource SourceFor(bool windows, bool obsRunning) =>
        windows ? AudioDeviceSource.Wasapi
        : obsRunning ? AudioDeviceSource.Obs
        : AudioDeviceSource.None;

    internal IReadOnlyList<AudioDeviceSetting> Snapshot
    {
        get
        {
            lock (_gate)
                return _snapshot;
        }
    }

    internal bool Refresh()
    {
        var inputs = TryEnumerate(AudioSourceKind.Input);
        var outputs = TryEnumerate(AudioSourceKind.Output);

        lock (_gate)
        {
            if (inputs.Success)
                _inputs = Normalize(inputs.Devices);
            if (outputs.Success)
                _outputs = Normalize(outputs.Devices);
            if (!inputs.Success && !outputs.Success)
                return false;

            var next = _inputs.Concat(_outputs)
                .OrderBy(device => device.Direction)
                .ThenBy(device => device.Id, StringComparer.Ordinal)
                .ToArray();
            if (Equivalent(_snapshot, next))
                return false;
            _snapshot = next;
            return true;
        }
    }

    private (bool Success, IReadOnlyList<AudioDeviceSetting> Devices) TryEnumerate(AudioSourceKind kind)
    {
        try
        {
            return _enumerate(kind);
        }
        catch
        {
            return (false, []);
        }
    }

    private static AudioDeviceSetting[] Normalize(IEnumerable<AudioDeviceSetting> devices) => devices
        .Where(device => device is not null)
        .Select(device => new AudioDeviceSetting
        {
            Id = device.Id,
            Name = device.Name,
            Direction = device.Direction,
        })
        .OrderBy(device => device.Id, StringComparer.Ordinal)
        .ToArray();

    private static (bool Success, IReadOnlyList<AudioDeviceSetting> Devices) EnumerateWasapi(AudioSourceKind kind)
    {
        var success = WasapiDeviceEnumerator.TryEnumerate(kind, out var devices);
        return (success, devices);
    }

    private static (bool Success, IReadOnlyList<AudioDeviceSetting> Devices) EnumerateObs(AudioSourceKind kind)
    {
        var success = ObsAudioDeviceEnumerator.TryEnumerate(kind, out var devices);
        return (success, devices);
    }

    private static bool Equivalent(IReadOnlyList<AudioDeviceSetting> left,
        IReadOnlyList<AudioDeviceSetting> right)
    {
        if (left.Count != right.Count)
            return false;
        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index].Id, right[index].Id, StringComparison.Ordinal) ||
                !string.Equals(left[index].Name, right[index].Name, StringComparison.Ordinal) ||
                left[index].Direction != right[index].Direction)
                return false;
        }
        return true;
    }
}

internal enum AudioDeviceSource
{
    None,
    Wasapi,
    Obs,
}
