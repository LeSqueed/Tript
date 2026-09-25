// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;
using Tript.Settings;
using Xunit;

namespace Tript.Recorder.Tests;

public sealed class ObsAudioDeviceEnumeratorTests
{
    private static ObsSourceProperty DeviceList(params (string? Value, string? Name)[] items) =>
        new("device_id", ObsPropertyType.List,
            items.Select(item => new ObsSourcePropertyItem(item.Value, ObsComboFormat.String, item.Name)).ToList());

    [Fact]
    public void DescribeDevices_ListsEachDeviceWithItsDescriptionAndDirection()
    {
        var devices = ObsAudioDeviceEnumerator.DescribeDevices(AudioSourceKind.Input,
        [
            DeviceList(
                ("alsa_input.usb-Blue_Yeti-00.analog-stereo", "Yeti Stereo Microphone"),
                ("alsa_input.pci-0000_0c_00.4.analog-stereo", "Starship Line In")),
        ]);

        Assert.Equal(["alsa_input.usb-Blue_Yeti-00.analog-stereo", "alsa_input.pci-0000_0c_00.4.analog-stereo"],
            devices.Select(device => device.Id));
        Assert.Equal(["Yeti Stereo Microphone", "Starship Line In"], devices.Select(device => device.Name));
        Assert.All(devices, device => Assert.Equal(AudioSourceKind.Input, device.Direction));
    }

    [Fact]
    public void DescribeDevices_LeavesOutTheDefaultEntry_BecauseNoDeviceIdAlreadyMeansDefault()
    {
        var devices = ObsAudioDeviceEnumerator.DescribeDevices(AudioSourceKind.Output,
            [DeviceList(("default", "Default"), ("alsa_output.pci.analog-stereo.monitor", "Monitor of Speakers"))]);

        Assert.Equal("alsa_output.pci.analog-stereo.monitor", Assert.Single(devices).Id);
    }

    [Fact]
    public void DescribeDevices_LeavesMonitorSourcesOutOfTheInputList()
    {
        var devices = ObsAudioDeviceEnumerator.DescribeDevices(AudioSourceKind.Input,
            [DeviceList(("alsa_input.usb-mic", "Mic"), ("alsa_output.pci.analog-stereo.monitor", "Monitor of Speakers"))]);

        Assert.Equal("alsa_input.usb-mic", Assert.Single(devices).Id);
    }

    [Fact]
    public void DescribeDevices_KeepsMonitorSourcesInTheOutputList()
    {
        var devices = ObsAudioDeviceEnumerator.DescribeDevices(AudioSourceKind.Output,
            [DeviceList(("alsa_output.pci.analog-stereo.monitor", "Monitor of Speakers"))]);

        var device = Assert.Single(devices);
        Assert.Equal("alsa_output.pci.analog-stereo.monitor", device.Id);
        Assert.Equal(AudioSourceKind.Output, device.Direction);
    }

    [Fact]
    public void DescribeDevices_NamesAnUndescribedDeviceAfterItsId()
    {
        var devices = ObsAudioDeviceEnumerator.DescribeDevices(AudioSourceKind.Input,
            [DeviceList(("alsa_input.usb-mic", null), ("alsa_input.line", "  "))]);

        Assert.Equal(["alsa_input.usb-mic", "alsa_input.line"], devices.Select(device => device.Name));
    }

    [Fact]
    public void DescribeDevices_SkipsEmptyAndRepeatedIds()
    {
        var devices = ObsAudioDeviceEnumerator.DescribeDevices(AudioSourceKind.Input,
            [DeviceList((null, "Nothing"), ("", "Blank"), ("alsa_input.usb-mic", "Mic"), ("alsa_input.usb-mic", "Mic again"))]);

        Assert.Equal("Mic", Assert.Single(devices).Name);
    }

    [Fact]
    public void DescribeDevices_ReadsOnlyTheDeviceIdProperty()
    {
        var devices = ObsAudioDeviceEnumerator.DescribeDevices(AudioSourceKind.Input,
        [
            new ObsSourceProperty("use_device_timing", ObsPropertyType.Bool, []),
            new ObsSourceProperty("other_list", ObsPropertyType.List,
                [new ObsSourcePropertyItem("not-a-device", ObsComboFormat.String, "Not a device")]),
        ]);

        Assert.Empty(devices);
    }
}
