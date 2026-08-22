// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Xunit;

namespace Tript.App.Tests;

[Collection(AppHostCollection.Name)]
public sealed class GeneralSettingsWireTests
{
    private readonly string _contentRoot;
    private readonly string _settingsPath;

    public GeneralSettingsWireTests(AppHostCollectionFixture fixture)
    {
        var testName = GetType().Name;
        _contentRoot = fixture.NewContentRoot(testName);
        _settingsPath = fixture.NewSettingsPath(testName);
    }

    [Fact]
    public async Task ListSettings_IncludesGeneralDefaults()
    {
        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var scope = host;
        await host.ConnectWebSocketAsync();

        var settings = await ReceiveSettingsAsync(host);
        var general = settings.GetProperty("settings").GetProperty("general");

        Assert.False(general.GetProperty("startWithWindows").GetBoolean());
        Assert.Equal("Window", general.GetProperty("startupVisibility").GetString());
        Assert.Equal("Taskbar", general.GetProperty("minimizeBehavior").GetString());
        Assert.Equal("Exit", general.GetProperty("closeBehavior").GetString());
        Assert.True(general.GetProperty("notifications").GetProperty("enabled").GetBoolean());

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task UpdateSettings_PersistsAndEchoesGeneralPage()
    {
        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var scope = host;
        await host.ConnectWebSocketAsync();
        await ReceiveSettingsAsync(host);

        await host.SendAsync("""
            {"method":"UpdateSettings","parameters":{"settings":{"general":{
              "startWithWindows":true,"startupVisibility":"tray",
              "minimizeBehavior":"tray","closeBehavior":"hideToTray",
              "notifications":{"enabled":false,"errors":false}
            }}}}
            """);

        var general = (await ReceiveSettingsAsync(host)).GetProperty("settings").GetProperty("general");
        Assert.True(general.GetProperty("startWithWindows").GetBoolean());
        Assert.Equal("Tray", general.GetProperty("startupVisibility").GetString());
        Assert.Equal("Tray", general.GetProperty("minimizeBehavior").GetString());
        Assert.Equal("HideToTray", general.GetProperty("closeBehavior").GetString());
        Assert.False(general.GetProperty("notifications").GetProperty("enabled").GetBoolean());
        Assert.False(general.GetProperty("notifications").GetProperty("errors").GetBoolean());

        var onDisk = JsonDocument.Parse(File.ReadAllText(_settingsPath)).RootElement.GetProperty("general");
        Assert.Equal("Tray", onDisk.GetProperty("startupVisibility").GetString());

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task APartialNotificationPatch_PreservesOtherNotificationValues()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath, """
            {"general":{"notifications":{"recordingStarted":false,"futureNotification":true}}}
            """);

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var scope = host;
        await host.ConnectWebSocketAsync();
        await ReceiveSettingsAsync(host);

        await host.SendAsync("""
            {"method":"UpdateSettings","parameters":{"settings":{"general":{"notifications":{"enabled":false}}}}}
            """);

        var notifications = (await ReceiveSettingsAsync(host))
            .GetProperty("settings").GetProperty("general").GetProperty("notifications");
        Assert.False(notifications.GetProperty("enabled").GetBoolean());
        Assert.False(notifications.GetProperty("recordingStarted").GetBoolean());
        Assert.True(notifications.GetProperty("futureNotification").GetBoolean());

        await host.ShutdownAsync();
    }

    private static async Task<JsonElement> ReceiveSettingsAsync(AppHostDriver host)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var (method, content) = await host.ReceiveAsyncParsed();
            if (method == "settings")
                return content;
        }

        throw new InvalidOperationException("No settings push arrived.");
    }
}
