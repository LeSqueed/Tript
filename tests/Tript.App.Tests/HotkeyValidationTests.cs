// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.Settings;
using Xunit;
using SettingsModel = Tript.Settings.Settings;

namespace Tript.App.Tests;

public sealed class HotkeyValidationTests
{
    [Fact]
    public void DefaultSettings_AreValid()
    {
        Assert.True(AppHost.ValidateHotkeys(new SettingsModel(), out var failure));
        Assert.Null(failure);
    }

    [Fact]
    public void DuplicateBindings_AreRejected()
    {
        var settings = new SettingsModel();
        settings.Hotkeys.ManualBookmark = new HotkeyBinding { Modifiers = ["Control", "Alt"], Key = "KeyR" };

        Assert.False(AppHost.ValidateHotkeys(settings, out var failure));
        Assert.Contains("ManualBookmark", failure);
        Assert.Contains("ToggleRecording", failure);
    }

    [Fact]
    public void DuplicateBindings_AreDetectedRegardlessOfModifierOrder()
    {
        var settings = new SettingsModel();
        settings.Hotkeys.ManualBookmark = new HotkeyBinding { Modifiers = ["Alt", "Control"], Key = "KeyR" };

        Assert.False(AppHost.ValidateHotkeys(settings, out _));
    }

    [Fact]
    public void UnboundActions_NeverCollide()
    {
        var settings = new SettingsModel();
        settings.Hotkeys.ManualBookmark = new HotkeyBinding { Modifiers = [], Key = null };
        settings.Hotkeys.QuickClip = new HotkeyBinding { Modifiers = [], Key = null };

        Assert.True(AppHost.ValidateHotkeys(settings, out var failure));
        Assert.Null(failure);
    }

    [Fact]
    public void ABindingWithNoModifiers_IsRejected()
    {
        var settings = new SettingsModel();
        settings.Hotkeys.QuickClip = new HotkeyBinding { Modifiers = [], Key = "KeyC" };

        Assert.False(AppHost.ValidateHotkeys(settings, out var failure));
        Assert.Contains("QuickClip", failure);
        Assert.Contains("modifier", failure, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class HotkeySettingsUpdateTests : IDisposable
{
    private readonly string _contentRoot;
    private readonly string _settingsPath;

    public HotkeySettingsUpdateTests()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), "tript-app-tests",
            nameof(HotkeySettingsUpdateTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_contentRoot);
        _settingsPath = Path.Combine(_contentRoot, "settings.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_contentRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void ARejectedDuplicateHotkeyPatch_IsNotPersisted()
    {
        Seed("""{"hotkeys":{"manualBookmark":{"modifiers":["Control","Alt"],"key":"KeyB"}}}""");
        var (store, host) = NewHost();
        using var scope = host;

        host.UpdateSettings(JsonSerializer.SerializeToElement(new
        {
            hotkeys = new
            {
                manualBookmark = new { modifiers = new[] { "Control", "Alt" }, key = "KeyR" },
            },
        }));

        Assert.Equal("KeyB", store.Load().Hotkeys.ManualBookmark.Key);

        using var onDisk = JsonDocument.Parse(File.ReadAllText(_settingsPath));
        Assert.Equal("KeyB", onDisk.RootElement.GetProperty("hotkeys")
            .GetProperty("manualBookmark").GetProperty("key").GetString());
    }

    [Fact]
    public void AValidHotkeyPatch_IsAcceptedAndStored()
    {
        var (store, host) = NewHost();
        using var scope = host;

        host.UpdateSettings(JsonSerializer.SerializeToElement(new
        {
            hotkeys = new
            {
                quickClip = new { modifiers = new[] { "Control", "Shift" }, key = "KeyK" },
                quickClipSeconds = 15,
            },
        }));

        Assert.Equal("KeyK", store.Load().Hotkeys.QuickClip.Key);
        Assert.Equal(15, store.Load().Hotkeys.QuickClipSeconds);

        using var onDisk = JsonDocument.Parse(File.ReadAllText(_settingsPath));
        var quickClip = onDisk.RootElement.GetProperty("hotkeys").GetProperty("quickClip");
        Assert.Equal("KeyK", quickClip.GetProperty("key").GetString());
    }

    private void Seed(string json) => File.WriteAllText(_settingsPath, json);

    private (SettingsStore Store, AppHost Host) NewHost()
    {
        var store = new SettingsStore(new SettingsFileProvider(_settingsPath));
        var host = new AppHost(new AppOptions
        {
            ContentRoot = _contentRoot,
            SettingsPath = _settingsPath,
            WebRoot = _contentRoot,
            FakeRecorder = true,
        }, store, runtime: null, new RecordingSessionTracker());
        return (store, host);
    }
}
