// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

// The SetVideoLocation seam (the Browse button on the recording page). The host never opens a
// dialog itself: it exposes a FolderPicker delegate that the desktop shell installs once the
// Photino window exists, and RequestVideoLocation runs the picker (when one is installed), then
// applies the picked directory through the ordinary UpdateSettings path — saving the settings file
// and pushing the new value to every client, exactly as if the user had typed the path.
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
        // A directory under the temp content root: the host creates it when the picked path
        // becomes the effective root, so it must be a location this process may write.
        var picked = Path.Combine(_contentRoot, "picked-recordings");
        _host.FolderPicker = () => picked;

        _host.RequestVideoLocation();

        // The picked path became the configured output directory, persisted to the settings file
        // and effective immediately (the host's content root follows it).
        Assert.Equal(picked, _store.Load().Recording.OutputDirectory);
        Assert.Equal(picked, _host.EffectiveRoot);
        var onDisk = System.Text.Json.JsonDocument.Parse(File.ReadAllText(_settingsPath));
        Assert.Equal(picked,
            onDisk.RootElement.GetProperty("recording").GetProperty("outputDirectory").GetString());
    }

    [Fact]
    public void RequestVideoLocation_Cancelled_LeavesSettingsUntouched()
    {
        // A cancelled picker returns null; the current setting stays.
        _host.FolderPicker = () => null;

        _host.RequestVideoLocation();

        Assert.Null(_store.Load().Recording.OutputDirectory);
        Assert.False(File.Exists(_settingsPath), "a cancelled picker must not write the settings file");
    }

    [Fact]
    public void RequestVideoLocation_WithoutPicker_IsANoOp()
    {
        // The headless host has no window, so FolderPicker is never installed; the command must
        // not throw and must not change anything.
        Assert.Null(_host.FolderPicker);

        _host.RequestVideoLocation();

        Assert.Null(_store.Load().Recording.OutputDirectory);
    }

    [Fact]
    public void RequestVideoLocation_PickerFailure_IsHandled()
    {
        // A picker that throws (a refused dialog) must not take the host down; the user stays
        // where they were.
        _host.FolderPicker = () => throw new InvalidOperationException("no native dialog");

        _host.RequestVideoLocation();

        Assert.Null(_store.Load().Recording.OutputDirectory);
    }
}
