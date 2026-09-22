// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

public sealed class AutomaticUpdateConsentTests : IDisposable
{
    private readonly string _root;
    private readonly SettingsStore _store;
    private readonly AppHost _host;

    public AutomaticUpdateConsentTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tript-update-consent", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var settingsPath = Path.Combine(_root, "settings.json");
        _store = new SettingsStore(new SettingsFileProvider(settingsPath));
        _host = new AppHost(new AppOptions
        {
            ContentRoot = _root,
            SettingsPath = settingsPath,
            WebRoot = _root,
            FakeRecorder = true,
        }, _store, runtime: null, new RecordingSessionTracker(), recorderStopTimeout: TimeSpan.FromMilliseconds(50));
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

    // The toggle was only read once at startup while the daily timer ignored it, so switching it off
    // still downloaded an update the next day. The gate must follow the setting as it is right now.
    [Fact]
    public void TurningAutomaticUpdatesOffTakesEffectWithoutARestart()
    {
        Assert.True(_host.AutomaticUpdateChecksEnabled);

        Assert.True(_store.TryUpdate(settings =>
        {
            settings.General.CheckForUpdatesAutomatically = false;
            return null;
        }, out _, out _));

        Assert.False(_host.AutomaticUpdateChecksEnabled);
    }
}
