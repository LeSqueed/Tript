// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Reflection;
using Tript.Settings;
using Tript.Recorder;
using Xunit;
using RecorderStateMachine = Tript.Recorder.Recorder;

namespace Tript.App.Tests;

public sealed class RecorderLifecycleTests : IDisposable
{
    private readonly string _root;
    private readonly AppHost _host;

    public RecorderLifecycleTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tript-recorder-lifecycle", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var settingsPath = Path.Combine(_root, "settings.json");
        _host = new AppHost(new AppOptions
        {
            ContentRoot = _root,
            SettingsPath = settingsPath,
            WebRoot = _root,
            FakeRecorder = true,
        }, new SettingsStore(new SettingsFileProvider(settingsPath)), runtime: null, new RecordingSessionTracker(),
            recorderStopTimeout: TimeSpan.FromMilliseconds(20));
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
    public void ConcurrentStarts_OnlyOneWins()
    {
        const int threads = 8;
        var ready = new CountdownEvent(threads);
        var go = new ManualResetEventSlim();
        var won = 0;

        var workers = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
        {
            ready.Signal();
            go.Wait();
            if (_host.StartRecording("Overwatch"))
                Interlocked.Increment(ref won);
        })).ToList();

        foreach (var worker in workers)
            worker.Start();

        ready.Wait();
        go.Set();
        foreach (var worker in workers)
            worker.Join();

        Assert.Equal(1, Volatile.Read(ref won));
        Assert.True(_host.IsRecording);
    }

    [Fact]
    public void ConcurrentStops_OnlyOneWins()
    {
        Assert.True(_host.StartRecording("Overwatch"));

        const int threads = 8;
        var ready = new CountdownEvent(threads);
        var go = new ManualResetEventSlim();
        var won = 0;

        var workers = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
        {
            ready.Signal();
            go.Wait();
            if (_host.StopRecording())
                Interlocked.Increment(ref won);
        })).ToList();

        foreach (var worker in workers)
            worker.Start();

        ready.Wait();
        go.Set();
        foreach (var worker in workers)
            worker.Join();

        Assert.Equal(1, Volatile.Read(ref won));
        Assert.False(_host.IsRecording);
    }

    [Fact]
    public void StartAndStopFromManyThreads_LeaveACoherentState()
    {
        var stop = new ManualResetEventSlim();
        var failures = 0;

        var workers = Enumerable.Range(0, 6).Select(index => new Thread(() =>
        {
            while (!stop.IsSet)
            {
                try
                {
                    if (index % 2 == 0)
                        _host.StartRecording("Overwatch");
                    else
                        _host.StopRecording();
                }
                catch
                {
                    Interlocked.Increment(ref failures);
                }
            }
        })).ToList();

        foreach (var worker in workers)
            worker.Start();
        Thread.Sleep(300);
        stop.Set();
        foreach (var worker in workers)
            worker.Join();

        Assert.Equal(0, Volatile.Read(ref failures));

        _host.StopRecording();
        Assert.False(_host.IsRecording);
    }

    [Fact]
    public void ManualRecordingOfDetectedGame_StopsWhenItsProcessExits()
    {
        var process = new DetectedGameProcess("Overwatch", 4001, "Overwatch", @"C:\Games\Overwatch\Overwatch.exe");
        _host.TrackDetectedGameStarted(process);

        Assert.Equal("Overwatch", _host.CurrentDetectedGameId());
        Assert.True(_host.StartRecording(null));
        Assert.Equal("Overwatch", _host.CurrentGameId);
        Assert.True(_host.BackgroundWorkSuspendedForRecording);

        _host.DetectedGameStopped(process);

        Assert.False(_host.IsRecording);
        Assert.False(_host.BackgroundWorkSuspendedForRecording);
        Assert.Null(_host.CurrentDetectedGameId());
    }

    [Fact]
    public void AnUnrelatedDetectedProcessExit_DoesNotStopTheRecording()
    {
        var overwatch = new DetectedGameProcess("Overwatch", 4001, "Overwatch", @"C:\Games\Overwatch\Overwatch.exe");
        var doom = new DetectedGameProcess("doom", 4002, "doom", @"C:\Games\Doom\doom.exe");
        _host.TrackDetectedGameStarted(overwatch);
        Assert.True(_host.StartRecording(null));
        _host.TrackDetectedGameStarted(doom);

        _host.DetectedGameStopped(doom);

        Assert.True(_host.IsRecording);
        _host.DetectedGameStopped(overwatch);
        Assert.False(_host.IsRecording);
    }

    [Fact]
    public void RecordingOwnership_TransfersToAnotherProcessOfTheSameGame()
    {
        var first = new DetectedGameProcess("Overwatch", 4001, "Overwatch",
            @"C:\Games\Overwatch\Overwatch.exe", DateTimeOffset.UtcNow);
        var second = new DetectedGameProcess("Overwatch", 4002, "Overwatch",
            @"C:\Games\Overwatch\Overwatch.exe", DateTimeOffset.UtcNow.AddSeconds(1));
        _host.TrackDetectedGameStarted(first);
        Assert.True(_host.StartRecording(null));
        _host.TrackDetectedGameStarted(second);

        _host.DetectedGameStopped(first);

        Assert.True(_host.IsRecording);
        Assert.Equal("Overwatch", _host.CurrentDetectedGameId());
        _host.DetectedGameStopped(second);
        Assert.False(_host.IsRecording);
    }

    [Fact]
    public void ProcessStartTime_DistinguishesPidReuse()
    {
        var path = @"C:\Games\Overwatch\Overwatch.exe";
        var first = new DetectedGameProcess("Overwatch", 4001, "Overwatch", path,
            DateTimeOffset.FromUnixTimeSeconds(1));
        var replacement = first with { ProcessStartTime = DateTimeOffset.FromUnixTimeSeconds(2) };

        Assert.NotEqual(AppHost.DetecteeOwner(first), AppHost.DetecteeOwner(replacement));
    }

    [Fact]
    public void RecordingMovesToAnAlreadyRunningDifferentGame()
    {
        var overwatch = new DetectedGameProcess("Overwatch", 4001, "Overwatch",
            @"C:\Games\Overwatch\Overwatch.exe");
        var doom = new DetectedGameProcess("doom", 4002, "doom", @"C:\Games\Doom\doom.exe");
        _host.TrackDetectedGameStarted(overwatch);
        Assert.True(_host.StartRecording(null));
        _host.TrackDetectedGameStarted(doom);

        _host.DetectedGameStopped(overwatch);
        WaitUntil(() => _host.CurrentGameId == "doom");

        Assert.True(_host.IsRecording);
        Assert.Equal("doom", _host.CurrentGameId);
        _host.DetectedGameStopped(doom);
        WaitUntil(() => !_host.IsRecording);
        Assert.False(_host.IsRecording);
    }

    private static void WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
            Thread.Sleep(20);
        Assert.True(condition(), "The condition was not reached before the timeout.");
    }

    [Fact]
    public void StopTimeout_LeavesTheHostRecordingAndDoesNotFinalizeMetadata()
    {
        var session = new FakeRecorderSession();
        session.CompleteStopSynchronously = false;
        var recorder = new RecorderStateMachine(session, new ResolvedRecorderSettings
        {
            Mode = RecordingMode.Session,
            OutputPath = Path.Combine(_root, "timeout.mp4"),
            ResolutionWidth = 1920,
            ResolutionHeight = 1080,
            Fps = 60,
            Encoder = "x264",
            AudioTracks = []
        });
        Assert.True(recorder.Start(new ResolvedRecorderSettings
        {
            Mode = RecordingMode.Session,
            OutputPath = Path.Combine(_root, "timeout.mp4"),
            ResolutionWidth = 1920,
            ResolutionHeight = 1080,
            Fps = 60,
            Encoder = "x264",
            AudioTracks = []
        }));

        var field = typeof(AppHost).GetField("_recorder", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(_host, recorder);

        Assert.False(_host.StopRecording());
        Assert.Equal(RecorderState.Stopping, recorder.Snapshot.State);
        Assert.True(_host.IsRecording);
    }

    [Fact]
    public void StopCompletionAfterTimeout_FinalizesTheHost()
    {
        var session = new FakeRecorderSession
        {
            CompleteStopSynchronously = false
        };
        var recorder = new RecorderStateMachine(session, new ResolvedRecorderSettings
        {
            Mode = RecordingMode.Session,
            OutputPath = Path.Combine(_root, "late-stop.mp4"),
            ResolutionWidth = 1920,
            ResolutionHeight = 1080,
            Fps = 60,
            Encoder = "x264",
            AudioTracks = []
        });
        Assert.True(recorder.Start(new ResolvedRecorderSettings
        {
            Mode = RecordingMode.Session,
            OutputPath = Path.Combine(_root, "late-stop.mp4"),
            ResolutionWidth = 1920,
            ResolutionHeight = 1080,
            Fps = 60,
            Encoder = "x264",
            AudioTracks = []
        }));

        typeof(AppHost).GetField("_recorder", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_host, recorder);

        Assert.False(_host.StopRecording());
        session.CompleteStop();

        Assert.True(SpinWait.SpinUntil(() => !_host.IsRecording, TimeSpan.FromSeconds(2)));
    }
}
