// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Xunit;

namespace Tript.App.Tests;

[Collection(AppHostCollection.Name)]
public sealed class PlatformCapabilitiesTests(AppHostCollectionFixture fixture)
{
    [Fact]
    public void Windows_OffersEveryDesktopFeature()
    {
        var windows = PlatformCapabilities.Windows;

        Assert.Equal("windows", windows.Platform);
        Assert.True(windows.Tray && windows.StartWithSystem && windows.HideToTray && windows.ObsSharing
            && windows.GlobalHotkeys && windows.Notifications && windows.NotificationSounds);
        Assert.Null(windows.GlobalHotkeysNote);
    }

    [Fact]
    public void Linux_HasNoTrayAutostartOrSharing_AndWaitsForTheShellToReportTheRest()
    {
        var linux = PlatformCapabilities.Unix("linux");

        Assert.Equal("linux", linux.Platform);
        Assert.False(linux.Tray);
        Assert.False(linux.StartWithSystem);
        Assert.False(linux.HideToTray);
        Assert.False(linux.ObsSharing);
        Assert.False(linux.GlobalHotkeys);
        Assert.False(linux.Notifications);
        Assert.False(linux.NotificationSounds);
    }

    [Fact]
    public void TheCurrentOs_DecidesTheCapabilities() =>
        Assert.Equal(OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
            PlatformCapabilities.ForCurrentOs().Platform);

    [Fact]
    public void TheWireForm_UsesCamelCaseAndLeavesOutAnAbsentNote()
    {
        var wire = JsonSerializer.SerializeToElement(
            PlatformCapabilities.Unix("linux") with { GlobalHotkeys = true }, Wire.Options);

        Assert.Equal("linux", wire.GetProperty("platform").GetString());
        Assert.True(wire.GetProperty("globalHotkeys").GetBoolean());
        Assert.False(wire.GetProperty("startWithSystem").GetBoolean());
        Assert.False(wire.GetProperty("hideToTray").GetBoolean());
        Assert.False(wire.GetProperty("obsSharing").GetBoolean());
        Assert.False(wire.GetProperty("notificationSounds").GetBoolean());
        Assert.False(wire.TryGetProperty("globalHotkeysNote", out _));
    }

    [Fact]
    public void TheSettingsMessage_CarriesThePlatformCapabilities()
    {
        var message = SettingsMessage.Build(new Tript.Settings.Settings(), [], displays: null,
            availableEncoders: null, primaryDisplay: null, appVersion: null,
            platformCapabilities: PlatformCapabilities.Unix("linux"));

        var capabilities = message.GetProperty("platformCapabilities");
        Assert.Equal("linux", capabilities.GetProperty("platform").GetString());
        Assert.False(capabilities.GetProperty("tray").GetBoolean());
    }

    [Fact]
    public async Task ANewConnection_IsToldWhatThisPlatformOffers()
    {
        var name = nameof(ANewConnection_IsToldWhatThisPlatformOffers);
        var host = AppHostDriver.StartFake(fixture.NewContentRoot(name), fixture.NewSettingsPath(name));
        await using var scope = host;
        await host.ConnectWebSocketAsync();

        JsonElement? capabilities = null;
        for (var attempt = 0; attempt < 10 && capabilities is null; attempt++)
        {
            var (method, content) = await host.ReceiveAsyncParsed();
            if (method == "settings")
                capabilities = content.GetProperty("platformCapabilities");
        }

        Assert.NotNull(capabilities);
        Assert.Equal(PlatformCapabilities.ForCurrentOs().Platform,
            capabilities.Value.GetProperty("platform").GetString());
        Assert.Equal(OperatingSystem.IsWindows(), capabilities.Value.GetProperty("tray").GetBoolean());

        await host.ShutdownAsync();
    }
}
