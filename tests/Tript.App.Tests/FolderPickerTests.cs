// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

public sealed class FolderPickerTests : IDisposable
{
    private readonly string _contentRoot;
    private readonly string _settingsPath;
    private readonly SettingsStore _store;
    private readonly AppHost _host;

    public FolderPickerTests()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), "tript-app-tests", nameof(FolderPickerTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_contentRoot);
        _settingsPath = Path.Combine(_contentRoot, "settings.json");
        _store = new SettingsStore(new SettingsFileProvider(_settingsPath));

        _host = new AppHost(new AppOptions
        {
            ContentRoot = _contentRoot,
            SettingsPath = _settingsPath,
            WebRoot = _contentRoot,
            FakeRecorder = true,
        }, _store, runtime: null, new RecordingSessionTracker());
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public void RequestVideoLocation_WithPicker_AppliesPickedDirectory()
    {
        var picked = Path.Combine(_contentRoot, "picked-recordings");
        _host.FolderPicker = () => picked;

        _host.RequestVideoLocation();

        Assert.Equal(picked, _store.Load().Recording.OutputDirectory);
        Assert.Equal(picked, _host.EffectiveRoot);
        var onDisk = System.Text.Json.JsonDocument.Parse(File.ReadAllText(_settingsPath));
        Assert.Equal(picked,
            onDisk.RootElement.GetProperty("recording").GetProperty("outputDirectory").GetString());
    }

    [Fact]
    public void RequestVideoLocation_Cancelled_LeavesSettingsUntouched()
    {
        _host.FolderPicker = () => null;

        _host.RequestVideoLocation();

        Assert.Null(_store.Load().Recording.OutputDirectory);
        Assert.False(File.Exists(_settingsPath), "a cancelled picker must not write the settings file");
    }

    [Fact]
    public void RequestVideoLocation_WithoutPicker_IsANoOp()
    {
        Assert.Null(_host.FolderPicker);

        _host.RequestVideoLocation();

        Assert.Null(_store.Load().Recording.OutputDirectory);
    }

    [Fact]
    public void RequestVideoLocation_PickerFailure_IsHandled()
    {
        _host.FolderPicker = () => throw new InvalidOperationException("no native dialog");

        _host.RequestVideoLocation();

        Assert.Null(_store.Load().Recording.OutputDirectory);
    }
}
