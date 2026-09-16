// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System;
using System.IO;
using System.Threading;
using Tript.Detection;
using Tript.Obs;
using Xunit;

namespace Tript.Detection.Tests;

[Collection(ModelSessionCollection.Name)]
public sealed class DetectorStartTests
{
    private const string GameId = "57ZZVAZ0PJK8VQGPKB728QE57C";

    [Fact]
    public void StartAndStop_WithTheShippedModel_SubscribesAndReleasesEachTime()
    {
        Assert.True(File.Exists(ModelService.GetModelPath(GameId)), "the shipped model must be present");
        var source = new CountingFrameSource();
        FrameSourceRegistry.SetResolver(() => source);
        try
        {
            using var detector = new VisualEventDetector();

            detector.Start(GameId);
            Assert.Equal(1, source.Subscribed);
            detector.Stop();
            Assert.Equal(1, source.Released);

            detector.Start(GameId);
            detector.Stop();
            Assert.Equal(2, source.Subscribed);
            Assert.Equal(2, source.Released);
        }
        finally
        {
            FrameSourceRegistry.Reset();
        }
    }

    [Fact]
    public void Start_ForAGameWithNoEvents_FailsWithoutLeavingARunBehind()
    {
        var source = new CountingFrameSource();
        FrameSourceRegistry.SetResolver(() => source);
        try
        {
            using var detector = new VisualEventDetector();

            Assert.Throws<InvalidDataException>(() => detector.Start("no-such-game-" + Guid.NewGuid().ToString("N")));
            Assert.Equal(0, source.Subscribed);

            detector.Start(GameId);
            detector.Stop();
            Assert.Equal(1, source.Released);
        }
        finally
        {
            FrameSourceRegistry.Reset();
        }
    }

    private sealed class CountingFrameSource : IFrameSource
    {
        private int _subscribed;
        private int _released;

        internal int Subscribed => Volatile.Read(ref _subscribed);

        internal int Released => Volatile.Read(ref _released);

        public IFrameSubscription Subscribe(FramePixelFormat format, int width, int height,
            FrameCallback callback, uint frameRateDivisor)
        {
            Interlocked.Increment(ref _subscribed);
            return new Subscription(this);
        }

        public VideoTiming? GetVideoTiming() => null;

        private sealed class Subscription(CountingFrameSource owner) : IFrameSubscription
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    Interlocked.Increment(ref owner._released);
            }
        }
    }
}
