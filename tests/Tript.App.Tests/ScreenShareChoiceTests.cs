// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Core;
using Tript.Recorder;
using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

public sealed class ScreenShareChoiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _tokenPath;
    private readonly AppHost _host;
    private readonly List<string> _errors = [];

    public ScreenShareChoiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tript-screen-share", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var settingsPath = Path.Combine(_root, "settings.json");
        _tokenPath = Path.Combine(_root, PortalRestoreTokenStore.FileName);
        _host = new AppHost(new AppOptions
        {
            ContentRoot = _root,
            SettingsPath = settingsPath,
            WebRoot = _root,
            FakeRecorder = true,
        }, new SettingsStore(new SettingsFileProvider(settingsPath)), runtime: null, new RecordingSessionTracker(),
            recorderStopTimeout: TimeSpan.FromMilliseconds(50), storageProbe: AmpleStorage.Probe);
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
    public void ChoosingADifferentScreen_ForgetsTheRememberedChoice()
    {
        File.WriteAllText(_tokenPath, "remembered");

        _host.ForgetScreenShareChoice();

        Assert.False(File.Exists(_tokenPath));
        Assert.Empty(_errors);
    }

    [Fact]
    public void ChoosingADifferentScreenWhileRecording_IsRefusedAndKeepsTheChoice()
    {
        File.WriteAllText(_tokenPath, "remembered");
        Assert.True(_host.StartRecording(null));

        _host.ForgetScreenShareChoice();

        Assert.True(File.Exists(_tokenPath));
        Assert.Contains("Stop the recording", Assert.Single(_errors));
        _host.StopRecording();
    }

    [Fact]
    public void WithoutAPortalRuntime_TheScreenIsNotReportedAsChosenByTheDesktop() =>
        Assert.False(_host.Capabilities.ScreenChosenByDesktop);
}
