// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

public sealed class AudioDeviceInventoryTests
{
    [Fact]
    public void SuccessfulRefreshPopulatesAndNormalizesSnapshot()
    {
        var inventory = new AudioDeviceInventory(kind => (true, kind == AudioSourceKind.Input
            ? [Device("input", "Mic", AudioSourceKind.Input)]
            : [Device("output", "Speakers", AudioSourceKind.Output)]));

        Assert.True(inventory.Refresh());
        Assert.Equal(["input", "output"], inventory.Snapshot.Select(device => device.Id));
    }

    [Fact]
    public void FailedRefreshKeepsLastKnownGoodSnapshot()
    {
        var fail = false;
        var inventory = new AudioDeviceInventory(kind => fail
            ? (false, Array.Empty<AudioDeviceSetting>())
            : (true, kind == AudioSourceKind.Input
                ? [Device("input", "Mic", AudioSourceKind.Input)]
                : Array.Empty<AudioDeviceSetting>()));

        Assert.True(inventory.Refresh());
        fail = true;
        Assert.False(inventory.Refresh());
        Assert.Equal("input", Assert.Single(inventory.Snapshot).Id);
    }

    [Fact]
    public void ProviderExceptionKeepsLastKnownGoodSnapshot()
    {
        var fail = false;
        var inventory = new AudioDeviceInventory(kind => fail
            ? throw new InvalidOperationException()
            : (true, kind == AudioSourceKind.Input
                ? [Device("input", "Mic", AudioSourceKind.Input)]
                : Array.Empty<AudioDeviceSetting>()));

        inventory.Refresh();
        fail = true;

        Assert.False(inventory.Refresh());
        Assert.Equal("input", Assert.Single(inventory.Snapshot).Id);
    }

    [Fact]
    public void SuccessfulEmptyRefreshClearsSnapshot()
    {
        var empty = false;
        var inventory = new AudioDeviceInventory(kind => (true, !empty && kind == AudioSourceKind.Input
            ? [Device("input", "Mic", AudioSourceKind.Input)]
            : Array.Empty<AudioDeviceSetting>()));

        inventory.Refresh();
        empty = true;

        Assert.True(inventory.Refresh());
        Assert.Empty(inventory.Snapshot);
    }

    [Fact]
    public void EquivalentReorderedInventoryDoesNotReportAChange()
    {
        var reverse = false;
        var first = Device("input-a", "Mic A", AudioSourceKind.Input);
        var second = Device("input-b", "Mic B", AudioSourceKind.Input);
        var inventory = new AudioDeviceInventory(kind => (true, kind == AudioSourceKind.Output
            ? Array.Empty<AudioDeviceSetting>()
            : reverse ? [second, first] : [first, second]));

        Assert.True(inventory.Refresh());
        reverse = true;
        Assert.False(inventory.Refresh());
    }

    [Fact]
    public void FailedOutputRefreshDoesNotBlockInputHotPlug()
    {
        var includeSecondInput = false;
        var inventory = new AudioDeviceInventory(kind => kind == AudioSourceKind.Output
            ? (false, Array.Empty<AudioDeviceSetting>())
            : (true, includeSecondInput
                ? [Device("input-a", "Mic A", kind), Device("input-b", "Mic B", kind)]
                : [Device("input-a", "Mic A", kind)]));

        Assert.True(inventory.Refresh());
        includeSecondInput = true;

        Assert.True(inventory.Refresh());
        Assert.Equal(2, inventory.Snapshot.Count);
    }

    [Theory]
    [InlineData(true, true, nameof(AudioDeviceSource.Wasapi))]
    [InlineData(true, false, nameof(AudioDeviceSource.Wasapi))]
    [InlineData(false, true, nameof(AudioDeviceSource.Obs))]
    [InlineData(false, false, nameof(AudioDeviceSource.None))]
    public void SourceFor_AsksWasapiOnWindows_AndLibobsElsewhereOnceItRuns(bool windows, bool obsRunning,
        string expected)
    {
        Assert.Equal(expected, AudioDeviceInventory.SourceFor(windows, obsRunning).ToString());
    }

    private static AudioDeviceSetting Device(string id, string name, AudioSourceKind direction) => new()
    {
        Id = id,
        Name = name,
        Direction = direction,
    };
}
