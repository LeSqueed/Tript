// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Reflection;
using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

public sealed class GraphicsRecoveryTests : IDisposable
{
    private const string DeviceRemovedLine = "  Device Removed Reason: 887A0007";

    private readonly string _root;
    private readonly GraphicsDeviceMonitor _monitor = new();
    private readonly AppHost _host;
    private readonly ManualResetEventSlim _restarted = new();
    private int _restarts;

    public GraphicsRecoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tript-graphics-recovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var settingsPath = Path.Combine(_root, "settings.json");
        _host = new AppHost(new AppOptions
        {
            ContentRoot = _root,
            SettingsPath = settingsPath,
            WebRoot = _root,
            FakeRecorder = true,
        }, new SettingsStore(new SettingsFileProvider(settingsPath)), runtime: null, new RecordingSessionTracker(),
            recorderStopTimeout: TimeSpan.FromMilliseconds(100),
            pendingStopFinalizeTimeout: TimeSpan.FromMilliseconds(300),
            storageProbe: AmpleStorage.Probe,
            graphicsDevice: _monitor)
        {
            RecoveryRestartMinimumUptime = TimeSpan.Zero,
        };
        _host.RestartRequested += () =>
        {
            Interlocked.Increment(ref _restarts);
            _restarted.Set();
        };
    }

    public void Dispose()
    {
        _host.Dispose();
        _restarted.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    [Theory]
    [InlineData("device_draw (D3D11): Failed to create blend state (887A0005)")]
    [InlineData("[game-capture: 'app capture'] init_shtex_capture: failed to open shared handle")]
    [InlineData("Skipping module 'graphics-hook64.dll', not an OBS plugin")]
    public void Monitor_IgnoresLinesThatDoNotReportADeviceRemoval(string line)
    {
        var monitor = new GraphicsDeviceMonitor();
        var raised = 0;
        monitor.Lost += () => raised++;

        monitor.Observe(line);

        Assert.False(monitor.IsLost);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void Monitor_ReportsTheLossOnce()
    {
        var monitor = new GraphicsDeviceMonitor();
        var raised = 0;
        monitor.Lost += () => raised++;

        monitor.Observe(DeviceRemovedLine);
        monitor.Observe(DeviceRemovedLine);

        Assert.True(monitor.IsLost);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void ALostDevice_RefusesNewRecordings_AndRequestsARestart()
    {
        _monitor.Observe(DeviceRemovedLine);

        Assert.True(_restarted.Wait(TimeSpan.FromSeconds(10)));
        Assert.False(_host.StartRecording("Overwatch"));
        Assert.False(_host.IsRecording);
        Assert.Equal(1, Volatile.Read(ref _restarts));
    }

    [Fact]
    public void ALostDeviceDuringARecording_StopsItBeforeRestarting()
    {
        Assert.True(_host.StartRecording("Overwatch"));

        _monitor.Observe(DeviceRemovedLine);

        Assert.True(_restarted.Wait(TimeSpan.FromSeconds(10)));
        Assert.False(_host.IsRecording);
    }

    [Fact]
    public void ALostDeviceShortlyAfterStart_DoesNotRestartInALoop()
    {
        _host.RecoveryRestartMinimumUptime = TimeSpan.FromHours(1);

        _monitor.Observe(DeviceRemovedLine);

        Assert.False(_restarted.Wait(TimeSpan.FromSeconds(1)));
        Assert.False(_host.StartRecording("Overwatch"));
    }

    [Fact]
    public void AStopThatNeverCompletes_RequestsARestart()
    {
        Assert.True(_host.StartRecording("Overwatch"));
        var session = Field<FakeRecorderSession>(_host, "_recorderSession");
        var output = Field<FakeRecorderSession.FakeOutput>(session, "_output");
        output.CompleteStopSynchronously = false;

        Assert.False(_host.StopRecording());

        Assert.True(_restarted.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, Volatile.Read(ref _restarts));
    }
}
