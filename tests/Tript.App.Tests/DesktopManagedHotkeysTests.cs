// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.Core;
using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

public sealed class DesktopManagedHotkeysTests : IDisposable
{
    private readonly string _root;
    private readonly AppHost _host;
    private readonly List<string> _errors = [];

    public DesktopManagedHotkeysTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tript-desktop-hotkeys", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var settingsPath = Path.Combine(_root, "settings.json");
        _host = new AppHost(new AppOptions
        {
            ContentRoot = _root,
            SettingsPath = settingsPath,
            WebRoot = _root,
            FakeRecorder = true,
        }, new SettingsStore(new SettingsFileProvider(settingsPath)), runtime: null, new RecordingSessionTracker(),
            storageProbe: AmpleStorage.Probe);
        _host.NotificationRequested += (kind, _, body) =>
        {
            if (kind == NotificationKind.Error)
                _errors.Add(body);
        };
    }

    public void Dispose()
    {
        _host.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void ThePortalBackend_IsReportedAsDesktopManagedAndConfigurable()
    {
        _host.ReportGlobalHotkeys(true, "note", managedByDesktop: true, configurable: true);

        Assert.True(_host.Capabilities.GlobalHotkeysManagedByDesktop);
        Assert.True(_host.Capabilities.GlobalHotkeysConfigurable);
    }

    [Fact]
    public void TheTriggersTheDesktopAssigned_AreCarriedPerActionOnTheWire()
    {
        _host.ReportDesktopHotkeyTriggers(new Dictionary<HotkeyAction, string>
        {
            [HotkeyAction.ToggleRecording] = "Meta+F9",
            [HotkeyAction.QuickClip] = "Ctrl+Alt+C",
        });

        var wire = JsonSerializer.SerializeToElement(_host.Capabilities, Wire.Options)
            .GetProperty("desktopHotkeyTriggers");
        Assert.Equal("Meta+F9", wire.GetProperty("toggleRecording").GetString());
        Assert.Equal("Ctrl+Alt+C", wire.GetProperty("quickClip").GetString());
        Assert.False(wire.TryGetProperty("manualBookmark", out _));
    }

    [Fact]
    public void ReportingTheSameTriggersAgain_LeavesTheCapabilitiesEqual()
    {
        var triggers = new Dictionary<HotkeyAction, string> { [HotkeyAction.ToggleRecording] = "Meta+F9" };
        _host.ReportDesktopHotkeyTriggers(triggers);
        var first = _host.Capabilities;

        _host.ReportDesktopHotkeyTriggers(new Dictionary<HotkeyAction, string>(triggers));

        Assert.Equal(first, _host.Capabilities);
    }

    [Fact]
    public void ConfiguringHotkeys_OpensTheDesktopsSettingsWhenThePortalCanDoIt()
    {
        var opened = 0;
        _host.GlobalHotkeyConfigurator = () => ++opened > 0;
        _host.ReportGlobalHotkeys(true, null, managedByDesktop: true, configurable: true);

        _host.ConfigureGlobalHotkeys();

        Assert.Equal(1, opened);
        Assert.Empty(_errors);
    }

    [Fact]
    public void ConfiguringHotkeys_DoesNothingWhenThePortalCannotConfigure()
    {
        var opened = 0;
        _host.GlobalHotkeyConfigurator = () => ++opened > 0;
        _host.ReportGlobalHotkeys(true, null, managedByDesktop: true, configurable: false);

        _host.ConfigureGlobalHotkeys();

        Assert.Equal(0, opened);
    }

    [Fact]
    public void ADesktopThatWillNotOpenItsSettings_TellsTheUserWhereToChangeTheShortcuts()
    {
        _host.GlobalHotkeyConfigurator = () => false;
        _host.ReportGlobalHotkeys(true, null, managedByDesktop: true, configurable: true);

        _host.ConfigureGlobalHotkeys();

        Assert.Contains("keyboard shortcut settings", Assert.Single(_errors));
    }
}
