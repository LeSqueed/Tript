// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.Recorder;
using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

public sealed class StreamingSettingsTests : IDisposable
{
    private readonly string _contentRoot;
    private readonly string _settingsPath;

    public StreamingSettingsTests()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), "tript-app-tests",
            nameof(StreamingSettingsTests), Guid.NewGuid().ToString("N"));
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
    public void AStreamingPatch_IsStored()
    {
        var (store, host) = NewHost();
        using var scope = host;

        host.UpdateSettings(JsonSerializer.SerializeToElement(new
        {
            streaming = new { shareEnabled = true, shareWhen = "Always", senderName = "Tript Game" },
        }));

        var streaming = store.Load().Streaming;
        Assert.True(streaming.ShareEnabled);
        Assert.Equal(StreamShareWhen.Always, streaming.ShareWhen);
        Assert.Equal("Tript Game", streaming.SenderName);

        using var onDisk = JsonDocument.Parse(File.ReadAllText(_settingsPath));
        Assert.True(onDisk.RootElement.GetProperty("streaming").GetProperty("shareEnabled").GetBoolean());
    }

    [Fact]
    public void ASenderNameOutsidePlainAscii_IsRejected()
    {
        var (store, host) = NewHost();
        using var scope = host;

        host.UpdateSettings(JsonSerializer.SerializeToElement(new
        {
            streaming = new { senderName = "Tript é" },
        }));

        Assert.Equal(StreamingSettings.DefaultSenderName, store.Load().Streaming.SenderName);
    }

    [Fact]
    public void TheFakeRecorder_ReportsSharingAsUnavailableOnceEnabled()
    {
        var (_, host) = NewHost();
        using var scope = host;

        Assert.Equal("off", host.BuildStreamerStatus().State);

        host.UpdateSettings(JsonSerializer.SerializeToElement(new
        {
            streaming = new { shareEnabled = true },
        }));

        var status = host.BuildStreamerStatus();
        Assert.Equal("unsupported", status.State);
        Assert.True(status.ShareEnabled);
        Assert.Equal(StreamingSettings.DefaultSenderName, status.SenderName);
        Assert.False(status.HookConflictSuspected);
    }

    [Fact]
    public void EveryShareState_HasAWireName()
    {
        var names = Enum.GetValues<StreamShareState>().Select(AppHost.WireState).ToList();
        Assert.Equal(["off", "unsupported", "waitingForObs", "waitingForCapture", "live", "failed"], names);
    }

    [Fact]
    public void TheConflictWarnings_UsePlainPunctuation()
    {
        Assert.DoesNotContain('\u2014', AppHost.HookConflictWarning);
        Assert.DoesNotContain('\u2014', AppHost.HookConflictFallbackWarning);
    }

    private (SettingsStore Store, AppHost Host) NewHost()
    {
        var store = new SettingsStore(new SettingsFileProvider(_settingsPath));
        var host = new AppHost(new AppOptions
        {
            ContentRoot = _contentRoot,
            SettingsPath = _settingsPath,
            WebRoot = _contentRoot,
            FakeRecorder = true,
        }, store, runtime: null, new RecordingSessionTracker(),
            storageProbe: AmpleStorage.Probe);
        return (store, host);
    }
}
