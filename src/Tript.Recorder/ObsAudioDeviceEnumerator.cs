// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;
using Tript.Settings;

namespace Tript.Recorder;

public static class ObsAudioDeviceEnumerator
{
    private const string DeviceIdProperty = "device_id";
    private const string DefaultDeviceId = "default";
    private const string MonitorSuffix = ".monitor";

    public static bool TryEnumerate(AudioSourceKind kind, out IReadOnlyList<AudioDeviceSetting> devices)
    {
        var sourceType = ObsAudioRoutingSink.DefaultSourceTypeId(kind);
        if (!ObsSourceProperties.EnumerateTypeIds().Contains(sourceType, StringComparer.Ordinal))
        {
            devices = [];
            return false;
        }

        devices = DescribeDevices(kind, ObsSourceProperties.EnumerateTypeProperties(sourceType));
        return true;
    }

    internal static IReadOnlyList<AudioDeviceSetting> DescribeDevices(AudioSourceKind kind,
        IReadOnlyList<ObsSourceProperty> properties)
    {
        var property = properties.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, DeviceIdProperty, StringComparison.Ordinal));
        if (property.Items is null)
            return [];

        var devices = new List<AudioDeviceSetting>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in property.Items)
        {
            if (item.Value is not string id || string.IsNullOrWhiteSpace(id)
                || string.Equals(id, DefaultDeviceId, StringComparison.Ordinal)
                || (kind == AudioSourceKind.Input && id.EndsWith(MonitorSuffix, StringComparison.Ordinal))
                || !seen.Add(id))
            {
                continue;
            }

            devices.Add(new AudioDeviceSetting
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(item.Name) ? id : item.Name,
                Direction = kind,
            });
        }

        return devices;
    }
}
