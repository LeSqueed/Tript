// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

// The recorder's own state is reached from three threads: the IPC dispatch pool, the game detector's
// timer, and the hook probe's timer. StartRecording is a check-then-act with a rebuild in the middle,
// so without a gate the losing thread drives a disposed session into libobs or two starts cross their
// metadata over one file.
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
        }, new SettingsStore(new SettingsFileProvider(settingsPath)), runtime: null, new RecordingSessionTracker());
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

    // Concurrent starts must not both win. Exactly one recording exists, so exactly one start may
    // report success — the loser has to see a recorder that is no longer Idle.
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

    // And the same for stops, so two threads cannot both write the metadata record for one session.
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

    // Start and stop hammered together must leave the host in a coherent state rather than a
    // half-torn-down one, and must not throw out of either entry point.
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

        // Whatever it settled on, the two views of "is a recording running" must agree.
        _host.StopRecording();
        Assert.False(_host.IsRecording);
    }
}
