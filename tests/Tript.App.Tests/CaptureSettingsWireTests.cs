// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Xunit;

namespace Tript.App.Tests;

[Collection(AppHostCollection.Name)]
public sealed class CaptureSettingsWireTests
{
    private readonly string _contentRoot;
    private readonly string _settingsPath;

    public CaptureSettingsWireTests(AppHostCollectionFixture fixture)
    {
        var testName = GetType().Name;
        _contentRoot = fixture.NewContentRoot(testName);
        _settingsPath = fixture.NewSettingsPath(testName);
    }

    [Fact]
    public async Task WithoutALibobsRuntime_TheDisplayFactsAreAbsentRatherThanEmpty()
    {
        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var scope = host;
        await host.ConnectWebSocketAsync();

        var settings = await ReceiveSettingsAsync(host);

        AssertNotEnumerated(settings, "availableDisplays");

        AssertNotEnumerated(settings, "displayFallbackWarning");

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task TheMonitorChoice_PersistsAsAnIdAndItsLabel()
    {
        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var scope = host;
        await host.ConnectWebSocketAsync();
        await ReceiveSettingsAsync(host);

        await host.SendAsync("""
            {"method":"UpdateSettings","parameters":{"settings":{"capture":
              {"method":"display","display":"monitor-2","displayLabel":"DP-1"}}}}
            """);

        var capture = (await ReceiveSettingsAsync(host)).GetProperty("settings").GetProperty("capture");

        Assert.Equal("Display", capture.GetProperty("method").GetString());
        Assert.Equal("monitor-2", capture.GetProperty("display").GetString());
        Assert.Equal("DP-1", capture.GetProperty("displayLabel").GetString());

        var onDisk = JsonDocument.Parse(File.ReadAllText(_settingsPath)).RootElement.GetProperty("capture");
        Assert.Equal("monitor-2", onDisk.GetProperty("display").GetString());
        Assert.Equal("DP-1", onDisk.GetProperty("displayLabel").GetString());

        Assert.False(onDisk.TryGetProperty("availableDisplays", out _));
        Assert.False(onDisk.TryGetProperty("displayFallbackWarning", out _));

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task TheGameCaptureTimeout_AcceptsWholeSecondsFromTheWire()
    {
        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var scope = host;
        await host.ConnectWebSocketAsync();
        await ReceiveSettingsAsync(host);

        await host.SendAsync("""
            {"method":"UpdateSettings","parameters":{"settings":{"game":{"gameCaptureTimeout":5000}}}}
            """);

        var game = (await ReceiveSettingsAsync(host)).GetProperty("settings").GetProperty("game");
        Assert.Equal(JsonValueKind.Number, game.GetProperty("gameCaptureTimeout").ValueKind);
        Assert.Equal(5000, game.GetProperty("gameCaptureTimeout").GetDouble());

        var onDisk = JsonDocument.Parse(File.ReadAllText(_settingsPath)).RootElement.GetProperty("game");
        Assert.Equal(JsonValueKind.Number, onDisk.GetProperty("gameCaptureTimeout").ValueKind);
        Assert.Equal(5000, onDisk.GetProperty("gameCaptureTimeout").GetDouble());

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task TheMachineFacts_AreNotWritableThroughUpdateSettings()
    {
        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var scope = host;
        await host.ConnectWebSocketAsync();
        await ReceiveSettingsAsync(host);

        await host.SendAsync("""
            {"method":"UpdateSettings","parameters":{"settings":{
              "availableDisplays":[{"id":"x","name":"x","width":1,"height":1,"primary":true}],
              "displayFallbackWarning":{"requestedId":"x"}}}}
            """);

        var settings = await ReceiveSettingsAsync(host);
        AssertNotEnumerated(settings, "availableDisplays");
        AssertNotEnumerated(settings, "displayFallbackWarning");

        var onDisk = JsonDocument.Parse(File.ReadAllText(_settingsPath)).RootElement;
        Assert.False(onDisk.TryGetProperty("availableDisplays", out _));
        Assert.False(onDisk.TryGetProperty("displayFallbackWarning", out _));

        await host.ShutdownAsync();
    }

    private static void AssertNotEnumerated(JsonElement settings, string field) =>
        Assert.True(
            !settings.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null,
            $"'{field}' should be absent or null on a host with no libobs, was {settings}");

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
