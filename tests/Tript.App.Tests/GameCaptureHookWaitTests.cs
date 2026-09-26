// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Reflection;
using Tript.Recorder;
using Tript.Settings;
using Tript.TestSupport;
using Xunit;
using RecorderStateMachine = Tript.Recorder.Recorder;

namespace Tript.App.Tests;

public sealed class GameCaptureHookWaitTests : IDisposable
{
    private readonly string _root;
    private readonly AppHost _host;

    public GameCaptureHookWaitTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tript-hook-wait", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var settingsPath = Path.Combine(_root, "settings.json");
        _host = new AppHost(new AppOptions
        {
            ContentRoot = _root,
            SettingsPath = settingsPath,
            WebRoot = _root,
            FakeRecorder = true,
        }, new SettingsStore(new SettingsFileProvider(settingsPath)), runtime: null, new RecordingSessionTracker(),
            recorderStopTimeout: TimeSpan.FromMilliseconds(20),
            storageProbe: AmpleStorage.Probe);
        _host.LinuxProcessFiles = new GameEnvironment(captureLayer: null);
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
    public void UnhookedGameWithDisplayFallback_StartsOnTheDisplayLayerAfterTheWait()
    {
        var session = new HookProbeSession
        {
            Policy = new CapturePolicy(DisplayCaptureMethod.Auto, null, TimeSpan.FromMilliseconds(300))
        };
        InjectRecorder(session);
        _host.TrackDetectedGameStarted(OverwatchProcess());

        Assert.True(_host.StartRecording("Overwatch"));

        Assert.True(_host.IsRecording);
        Assert.Equal("Overwatch", _host.CurrentGameId);
        Assert.False(session.Hooked);
        Assert.True(session.ClearSourceCalls >= 1);
        Assert.True(_host.StopRecording());
        Assert.False(_host.IsRecording);
    }

    [Fact]
    public void HookThatAttachesDuringTheWait_StartsOnGameCapture()
    {
        var session = new HookProbeSession
        {
            Policy = new CapturePolicy(DisplayCaptureMethod.Auto, null, TimeSpan.FromSeconds(5))
        };
        InjectRecorder(session);
        _host.TrackDetectedGameStarted(OverwatchProcess());
        var hooker = new Thread(() =>
        {
            session.WaitEntered.Wait(TimeSpan.FromSeconds(5));
            Thread.Sleep(200);
            session.Hooked = true;
        });
        hooker.Start();

        Assert.True(_host.StartRecording("Overwatch"));
        hooker.Join();

        Assert.True(session.WaitReturnedHooked);
        Assert.True(_host.IsRecording);
        Assert.True(_host.StopRecording());
    }

    [Fact]
    public void UnhookedGameWithoutDisplayFallback_KeepsWaitingUntilTheHookAttaches()
    {
        var session = new HookProbeSession
        {
            Policy = new CapturePolicy(DisplayCaptureMethod.Game, null, TimeSpan.FromMilliseconds(200)),
            HasDisplayFallback = false
        };
        InjectRecorder(session);
        _host.TrackDetectedGameStarted(OverwatchProcess());

        var startResult = false;
        var started = false;
        var startThread = new Thread(() =>
        {
            startResult = _host.StartRecording("Overwatch");
            started = true;
        });
        startThread.Start();

        Assert.True(session.WaitEntered.Wait(TimeSpan.FromSeconds(2)));
        Thread.Sleep(600);
        Assert.False(started);
        Assert.False(_host.IsRecording);

        session.Hooked = true;
        Assert.True(startThread.Join(TimeSpan.FromSeconds(3)));
        Assert.True(startResult);
        Assert.True(_host.IsRecording);
        Assert.True(_host.StopRecording());
    }

    [Fact]
    public void HookWait_DoesNotHoldTheRecorderGate()
    {
        var session = new HookProbeSession
        {
            Policy = new CapturePolicy(DisplayCaptureMethod.Game, null, TimeSpan.FromSeconds(30)),
            HasDisplayFallback = false
        };
        InjectRecorder(session);
        _host.TrackDetectedGameStarted(OverwatchProcess());

        var startResult = true;
        var startThread = new Thread(() => startResult = _host.StartRecording("Overwatch"));
        startThread.Start();
        Assert.True(session.WaitEntered.Wait(TimeSpan.FromSeconds(2)));

        var gate = (object)typeof(AppHost)
            .GetField("_recorderGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(_host)!;
        Assert.True(Monitor.TryEnter(gate, TimeSpan.FromSeconds(2)));
        try
        {
            Assert.False(_host.StopRecording());
        }
        finally
        {
            Monitor.Exit(gate);
        }

        Assert.True(startThread.Join(TimeSpan.FromSeconds(3)));
        Assert.False(startResult);
        Assert.False(_host.IsRecording);
    }

    [Fact]
    public void GameExitDuringTheHookWait_RefusesTheStart()
    {
        var session = new HookProbeSession
        {
            Policy = new CapturePolicy(DisplayCaptureMethod.Auto, null, TimeSpan.FromSeconds(5))
        };
        InjectRecorder(session);
        var process = OverwatchProcess();
        _host.TrackDetectedGameStarted(process);

        var startResult = true;
        var startThread = new Thread(() => startResult = _host.StartRecording("Overwatch"));
        startThread.Start();
        Assert.True(session.WaitEntered.Wait(TimeSpan.FromSeconds(2)));

        _host.DetectedGameStopped(process);

        Assert.True(startThread.Join(TimeSpan.FromSeconds(3)));
        Assert.False(startResult);
        Assert.False(_host.IsRecording);
        Assert.Null(_host.CurrentDetectedGameId());
    }

    [LinuxFact]
    public void AGameLaunchedWithTheCaptureLayer_WaitsPastTheUsualLimitForItsHook()
    {
        var session = new HookProbeSession
        {
            Policy = new CapturePolicy(DisplayCaptureMethod.Auto, null, TimeSpan.FromMilliseconds(100))
        };
        InjectRecorder(session);
        _host.LinuxProcessFiles = new GameEnvironment(captureLayer: true);
        _host.TrackDetectedGameStarted(OverwatchProcess());
        var hooker = new Thread(() =>
        {
            session.WaitEntered.Wait(TimeSpan.FromSeconds(5));
            Thread.Sleep(600);
            session.Hooked = true;
        });
        hooker.Start();

        Assert.True(_host.StartRecording("Overwatch"));
        hooker.Join();

        Assert.True(session.WaitReturnedHooked);
        Assert.Equal(GameCaptureWait.LaunchedForCaptureLimit, session.LastDeadline);
        Assert.True(_host.StopRecording());
    }

    [LinuxFact]
    public void AGameLaunchedWithoutTheCaptureLayer_RecordsTheScreenWithoutWaiting()
    {
        var session = new HookProbeSession
        {
            Policy = new CapturePolicy(DisplayCaptureMethod.Auto, null, TimeSpan.FromSeconds(5))
        };
        InjectRecorder(session);
        _host.LinuxProcessFiles = new GameEnvironment(captureLayer: false);
        _host.TrackDetectedGameStarted(OverwatchProcess());

        Assert.True(_host.StartRecording("Overwatch"));

        Assert.False(session.WaitEntered.IsSet);
        Assert.True(_host.IsRecording);
        Assert.True(_host.StopRecording());
    }

    private static DetectedGameProcess OverwatchProcess() =>
        new("Overwatch", 4001, "Overwatch", @"C:\Games\Overwatch\Overwatch.exe");

    private void InjectRecorder(HookProbeSession session)
    {
        var recorder = new RecorderStateMachine(session, new ResolvedRecorderSettings
        {
            Mode = RecordingMode.Session,
            OutputPath = Path.Combine(_root, "injected.mp4"),
            ResolutionWidth = 1920,
            ResolutionHeight = 1080,
            Fps = 60,
            Encoder = "x264",
            AudioTracks = []
        });
        typeof(AppHost).GetField("_recorder", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_host, recorder);
        typeof(AppHost).GetField("_recorderSession", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_host, session);
    }

    private sealed class HookProbeSession : IRecorderSession
    {
        private readonly FakeRecorderSession.FakeOutput _output = new();

        public CapturePolicy Policy { get; init; } = CapturePolicy.Default;

        public bool HasDisplayFallback { get; init; } = true;

        public bool HasGameCaptureSource => true;

        public ManualResetEventSlim WaitEntered { get; } = new(false);

        public volatile bool Hooked;

        public volatile bool WaitReturnedHooked;

        public int PlaceSourceCalls { get; private set; }

        public int ClearSourceCalls { get; private set; }

        public IRecorderOutput CreateOutput(ResolvedRecorderSettings settings) => _output;

        public void PlaceSourceOnChannel() => PlaceSourceCalls++;

        public void ClearSourceFromChannel() => ClearSourceCalls++;

        public TimeSpan? LastDeadline { get; private set; }

        public bool WaitForGameCapture(TimeSpan deadline, TimeSpan warningAfter, Action showWarning,
            Action clearWarning, CancellationToken cancellationToken)
        {
            LastDeadline = deadline;
            WaitEntered.Set();
            var started = DateTime.UtcNow;
            while (!cancellationToken.IsCancellationRequested)
            {
                if (Hooked)
                {
                    WaitReturnedHooked = true;
                    return true;
                }

                if (deadline != Timeout.InfiniteTimeSpan && DateTime.UtcNow - started >= deadline)
                    return false;

                Thread.Sleep(20);
            }

            return false;
        }

        public void Dispose()
        {
        }
    }

    private sealed class GameEnvironment(bool? captureLayer) : IProcessFiles
    {
        public string? ReadCommandLine(int processId) => null;

        public string? ReadExecutableLink(int processId) =>
            captureLayer is null ? null : "/usr/bin/wine64-preloader";

        public string? ReadEnvironmentVariable(int processId, string name) =>
            captureLayer == true && name == "OBS_VKCAPTURE" ? "1" : null;

        public string? ReadCommandName(int processId) => captureLayer is null ? null : "Overwatch.exe";
    }
}
