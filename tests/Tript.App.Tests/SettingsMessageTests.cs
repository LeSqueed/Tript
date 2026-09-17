// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.Obs;
using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

public sealed class SettingsMessageTests
{
    private static readonly ObsDisplay Primary = new("monitor-1", "DP-1", 0, 2560, 1440, Primary: true);

    [Fact]
    public void Build_AddsTheAudioDevicesToTheAudioPage()
    {
        var message = SettingsMessage.Build(new Tript.Settings.Settings(),
            [new AudioDeviceSetting { Id = "mic", Name = "Mic", Direction = AudioSourceKind.Input }],
            displays: null, availableEncoders: null, primaryDisplay: null, appVersion: "1.2.3");

        var device = Assert.Single(message.GetProperty("settings").GetProperty("audio").GetProperty("devices")
            .EnumerateArray());
        Assert.Equal("mic", device.GetProperty("id").GetString());
        Assert.Equal("1.2.3", message.GetProperty("appVersion").GetString());
        Assert.False(message.TryGetProperty("displayResolution", out _));
        Assert.False(message.TryGetProperty("availableDisplays", out _));
        Assert.False(message.TryGetProperty("displayFallbackWarning", out _));
    }

    [Fact]
    public void Build_DescribesTheDisplaysAndThePrimaryResolution()
    {
        var message = SettingsMessage.Build(new Tript.Settings.Settings(), [], [Primary], ["obs_x264"],
            new DisplaySize(1920, 1080), appVersion: null);

        var display = Assert.Single(message.GetProperty("availableDisplays").EnumerateArray());
        Assert.Equal("monitor-1", display.GetProperty("id").GetString());
        Assert.True(display.GetProperty("primary").GetBoolean());
        Assert.Equal(1920, message.GetProperty("displayResolution").GetProperty("width").GetInt32());
        Assert.Equal("obs_x264", Assert.Single(message.GetProperty("availableEncoders").EnumerateArray()).GetString());
    }

    [Fact]
    public void DisplayFallbackWarning_OnlyAppearsWhenTheChosenDisplayIsGone()
    {
        var present = new CaptureSettings { Display = "monitor-1" };
        var missing = new CaptureSettings { Display = "monitor-9", DisplayLabel = "HDMI-9" };

        Assert.Null(SettingsMessage.DisplayFallbackWarning(present, [Primary]));
        Assert.Null(SettingsMessage.DisplayFallbackWarning(missing, displays: null));
        Assert.Null(SettingsMessage.DisplayFallbackWarning(new CaptureSettings(), [Primary]));

        var warning = JsonSerializer.SerializeToElement(SettingsMessage.DisplayFallbackWarning(missing, [Primary]));
        Assert.Equal("monitor-9", warning.GetProperty("requestedId").GetString());
        Assert.Equal("HDMI-9", warning.GetProperty("requestedLabel").GetString());
        Assert.Equal("monitor-1", warning.GetProperty("usingId").GetString());
    }
}
