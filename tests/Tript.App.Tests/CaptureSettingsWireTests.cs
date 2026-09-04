// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Xunit;

namespace Tript.App.Tests;

// The capture half of the settings message: the monitor preference the user saves, and the two
// machine facts that ride alongside `settings` rather than inside it. A field that lands inside
// `settings` is round-tripped straight back into the settings file on the next save, which is why
// they are siblings — and why a test has to hold them there.
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

    // The fake-recorder host has no libobs to ask, and a P/Invoke there would segfault rather than
    // answer. "Could not enumerate" is what the client sees — the wire drops nulls, so it arrives as
    // an absent field rather than an empty list, exactly like availableEncoders.
    [Fact]
    public async Task WithoutALibobsRuntime_TheDisplayFactsAreAbsentRatherThanEmpty()
    {
        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var scope = host;
        await host.ConnectWebSocketAsync();

        var settings = await ReceiveSettingsAsync(host);

        AssertNotEnumerated(settings, "availableDisplays");

        // Nothing was enumerated, so nothing can be known to be missing.
        AssertNotEnumerated(settings, "displayFallbackWarning");

        await host.ShutdownAsync();
    }

    // The monitor choice is two fields: the id the capture plugin matches on, and the label that
    // exists only so a warning can name a monitor which is no longer attached.
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

        // Enum names are read leniently and written exactly — the push echoes "Display", not the
        // "display" the client sent.
        Assert.Equal("Display", capture.GetProperty("method").GetString());
        Assert.Equal("monitor-2", capture.GetProperty("display").GetString());
        Assert.Equal("DP-1", capture.GetProperty("displayLabel").GetString());

        var onDisk = JsonDocument.Parse(File.ReadAllText(_settingsPath)).RootElement.GetProperty("capture");
        Assert.Equal("monitor-2", onDisk.GetProperty("display").GetString());
        Assert.Equal("DP-1", onDisk.GetProperty("displayLabel").GetString());

        // The machine facts must never make it into the settings file, however they arrive.
        Assert.False(onDisk.TryGetProperty("availableDisplays", out _));
        Assert.False(onDisk.TryGetProperty("displayFallbackWarning", out _));

        await host.ShutdownAsync();
    }

    // The game-capture timeout is a `game` page field the capture page patches, and it arrives on
    // the wire as whole seconds: the settings UI sends a number and reads a number back. Before the
    // seconds converter existed the TimeSpan this value resolves to made the patch fail to
    // deserialize ("The JSON value could not be converted to System.TimeSpan") and refused the save.
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

    // A client that echoes the whole settings message back — the shape the settings UI actually
    // sends — must not be able to write the machine facts into the stored configuration.
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
