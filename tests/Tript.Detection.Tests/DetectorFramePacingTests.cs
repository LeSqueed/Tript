// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Tript.Detection;
using Tript.Obs;
using Xunit;

namespace Tript.Detection.Tests;

[Collection(ModelSessionCollection.Name)]
public sealed class DetectorFramePacingTests
{
    private const string GameId = "57ZZVAZ0PJK8VQGPKB728QE57C";
    private const int Width = 1920;
    private const int Height = 1080;

    [Fact]
    public void ALiveFeed_ProducesOneBatchPerInterval_FromFramesCopiedOnDemand()
    {
        var source = new PumpingFrameSource();
        FrameSourceRegistry.SetResolver(() => source);
        try
        {
            using var detector = new VisualEventDetector(detectionIntervalMs: 600);
            var batches = new ConcurrentQueue<DetectionBatch>();
            detector.DetectionsAvailable += batches.Enqueue;

            detector.Start(GameId);
            Thread.Sleep(TimeSpan.FromSeconds(3.2));
            detector.Stop();

            var copied = (int)typeof(VisualEventDetector)
                .GetField("_diagnosticFrameCount", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(detector)!;

            Assert.InRange(batches.Count, 3, 6);
            Assert.InRange(source.Delivered, 40, int.MaxValue);
            Assert.InRange(copied, batches.Count, batches.Count + 1);
            foreach (var batch in batches)
                Assert.NotEqual(default, batch.FrameTimestamp);
        }
        finally
        {
            FrameSourceRegistry.Reset();
            source.Dispose();
        }
    }

    [Fact]
    public void WithoutFrames_TheLoopKeepsWaitingAndReportsNothing()
    {
        var source = new PumpingFrameSource(pump: false);
        FrameSourceRegistry.SetResolver(() => source);
        try
        {
            using var detector = new VisualEventDetector(detectionIntervalMs: 300);
            var batches = 0;
            detector.DetectionsAvailable += _ => Interlocked.Increment(ref batches);

            detector.Start(GameId);
            Thread.Sleep(TimeSpan.FromSeconds(1.5));
            detector.Stop();

            Assert.Equal(0, batches);
        }
        finally
        {
            FrameSourceRegistry.Reset();
            source.Dispose();
        }
    }

    private sealed unsafe class PumpingFrameSource(bool pump = true) : IFrameSource, IDisposable
    {
        private readonly nint _plane = (nint)NativeMemory.AllocZeroed((nuint)(Width * Height * 4));
        private int _delivered;

        internal int Delivered => Volatile.Read(ref _delivered);

        public IFrameSubscription Subscribe(FramePixelFormat format, int width, int height,
            FrameCallback callback, uint frameRateDivisor) =>
            new Subscription(this, callback, pump);

        public VideoTiming? GetVideoTiming() => null;

        public void Dispose() => NativeMemory.Free((void*)_plane);

        private void Deliver(FrameCallback callback)
        {
            ReadOnlySpan<nint> planes = [_plane];
            ReadOnlySpan<uint> linesizes = [(uint)(Width * 4)];
            var frame = new VideoFrame(FramePixelFormat.Bgra, Width, Height, planes, linesizes);
            callback(in frame);
            Interlocked.Increment(ref _delivered);
        }

        private sealed class Subscription : IFrameSubscription
        {
            private readonly CancellationTokenSource _stop = new();
            private readonly Thread? _thread;

            internal Subscription(PumpingFrameSource owner, FrameCallback callback, bool pump)
            {
                if (!pump)
                    return;

                _thread = new Thread(() =>
                {
                    while (!_stop.Token.WaitHandle.WaitOne(20))
                        owner.Deliver(callback);
                })
                { IsBackground = true };
                _thread.Start();
            }

            public void Dispose()
            {
                _stop.Cancel();
                _thread?.Join();
                _stop.Dispose();
            }
        }
    }
}
