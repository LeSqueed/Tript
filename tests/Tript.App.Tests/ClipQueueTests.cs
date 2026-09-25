// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Collections.Concurrent;
using System.Reflection;
using Tript.Core;
using Tript.Media;
using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

public sealed class ClipQueueTests : IDisposable
{
    private readonly string _root;
    private readonly AppHost _host;

    public ClipQueueTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tript-clip-queue", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var settingsPath = Path.Combine(_root, "settings.json");
        var settings = new SettingsStore(new SettingsFileProvider(settingsPath));
        settings.Load();
        settings.Save();
        _host = new AppHost(new AppOptions
        {
            ContentRoot = _root,
            SettingsPath = settingsPath,
            WebRoot = _root,
            FakeRecorder = true,
        }, settings, runtime: null, new RecordingSessionTracker(),
            storageProbe: AmpleStorage.Probe);
    }

    [Fact]
    public void CreateClip_processes_requests_one_at_a_time_in_submission_order()
    {
        using var engine = new BlockingClipEngine();
        typeof(AppHost).GetField("_clipEngine", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_host, engine);

        _host.CreateClip(Request("clip-01"));
        Assert.True(engine.FirstStarted.Wait(TimeSpan.FromSeconds(5)));

        _host.CreateClip(Request("clip-02"));
        Assert.False(engine.SecondStarted.Wait(TimeSpan.FromMilliseconds(200)));

        engine.ReleaseFirst.Set();
        Assert.True(engine.Completed.Wait(TimeSpan.FromSeconds(5)));
        var queue = (SerialWorkQueue<ClipRequest>)typeof(AppHost).GetField("_clipQueue",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_host)!;
        Assert.True(SpinWait.SpinUntil(() => !queue.IsActive, TimeSpan.FromSeconds(5)));
        Assert.Equal(["clip-01", "clip-02"], engine.Order);
        Assert.Equal(1, engine.MaximumConcurrency);
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

    private ClipRequest Request(string id) => new()
    {
        OperationId = id,
        SourcePath = Path.Combine(_root, "session.mp4"),
        Regions = [],
        Mode = ClipMode.Combine,
        OutputPath = Path.Combine(_root, "clips", id + ".mp4"),
        Title = id,
    };

    private sealed class BlockingClipEngine : IClipEngine, IDisposable
    {
        private int _active;
        private int _completed;
        private int _maximumConcurrency;

        internal ManualResetEventSlim FirstStarted { get; } = new();
        internal ManualResetEventSlim SecondStarted { get; } = new();
        internal ManualResetEventSlim ReleaseFirst { get; } = new();
        internal ManualResetEventSlim Completed { get; } = new();
        internal ConcurrentQueue<string> Order { get; } = new();
        internal int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        public IReadOnlyList<string> CreateClips(ClipRequest request)
        {
            var concurrency = Interlocked.Increment(ref _active);
            Interlocked.Exchange(ref _maximumConcurrency,
                Math.Max(Volatile.Read(ref _maximumConcurrency), concurrency));
            try
            {
                Order.Enqueue(request.OperationId);
                if (request.OperationId == "clip-01")
                {
                    FirstStarted.Set();
                    ReleaseFirst.Wait(TimeSpan.FromSeconds(5));
                }
                else
                {
                    SecondStarted.Set();
                }

                Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath)!);
                File.WriteAllText(request.OutputPath, string.Empty);
                return [request.OutputPath];
            }
            finally
            {
                Interlocked.Decrement(ref _active);
                if (Interlocked.Increment(ref _completed) == 2)
                    Completed.Set();
            }
        }

        public void Dispose()
        {
            FirstStarted.Dispose();
            SecondStarted.Dispose();
            ReleaseFirst.Dispose();
            Completed.Dispose();
        }
    }
}
