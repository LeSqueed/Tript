// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

public sealed class UiLoadGatingTests : IDisposable
{
    private readonly string _root;
    private readonly AppHost _host;

    public UiLoadGatingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tript-ui-load-gating", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var settingsPath = Path.Combine(_root, "settings.json");
        var store = new SettingsStore(new SettingsFileProvider(settingsPath));
        store.Save();

        _host = new AppHost(new AppOptions
        {
            ContentRoot = _root,
            SettingsPath = settingsPath,
            WebRoot = _root,
            FakeRecorder = true,
        }, store, runtime: null, new RecordingSessionTracker(),
            recorderStopTimeout: TimeSpan.FromMilliseconds(50),
            storageProbe: AmpleStorage.Probe);
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
    public void AudioLevels_AreNotWantedUntilSomethingWatchesThem()
    {
        Assert.False(_host.AudioLevelsWanted);

        _host.WatchAudioLevels();

        Assert.True(_host.AudioLevelsWanted);
    }

    [Fact]
    public void Window_IsAssumedVisibleUntilTheShellSaysOtherwise()
    {
        Assert.True(_host.WindowVisible);

        _host.SetWindowVisible(false);
        Assert.False(_host.WindowVisible);

        _host.SetWindowVisible(true);
        Assert.True(_host.WindowVisible);
    }
}
