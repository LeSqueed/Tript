// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System;
using System.Buffers;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Tript.Detection;
using Xunit;

namespace Tript.Detection.Tests;

public class DetectionThreadLifecycleTests
{
    private static async Task<bool> Completes(Task work, TimeSpan timeout)
    {
        if (await Task.WhenAny(work, Task.Delay(timeout)) != work)
            return false;

        await work;
        return true;
    }

    private static Task<bool> CompletesWithin(Action action, TimeSpan timeout) =>
        Completes(Task.Run(action), timeout);

    [Fact]
    public async Task Stop_WithoutStart_DoesNotThrowOrBlock()
    {
        var detector = new VisualEventDetector();

        Assert.True(
            await CompletesWithin(detector.Stop, TimeSpan.FromSeconds(5)),
            "Stop() on a never-started detector blocked");
    }

    [Fact]
    public async Task StopCycling_WithoutStart_IsIdempotent()
    {
        var detector = new VisualEventDetector();

        for (int i = 0; i < 15; i++)
        {
            Assert.True(
                await CompletesWithin(detector.Stop, TimeSpan.FromSeconds(5)),
                $"Stop() blocked on cycle {i}");
        }
    }

    [Fact]
    public async Task Dispose_WithoutStart_DoesNotThrowOrBlock()
    {
        var detector = new VisualEventDetector();

        Assert.True(
            await CompletesWithin(detector.Dispose, TimeSpan.FromSeconds(5)),
            "Dispose() on a never-started detector blocked");
    }

    private static readonly Type FrameDataType =
        typeof(VisualEventDetector).GetNestedType("FrameData", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("VisualEventDetector.FrameData not found");

    private static readonly FieldInfo FrameDataBuffer =
        FrameDataType.GetField("_buffer", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("FrameData._buffer not found");

    private static FieldInfo Field(string name) =>
        typeof(VisualEventDetector).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"VisualEventDetector.{name} not found");

    private static object QueueFrame(VisualEventDetector detector, int size)
    {
        var frame = Activator.CreateInstance(FrameDataType, nonPublic: true)!;
        FrameDataBuffer.SetValue(frame, ArrayPool<byte>.Shared.Rent(size));

        var queue = Field("_frameQueue").GetValue(detector)!;
        var writer = queue.GetType().GetProperty("Writer")!.GetValue(queue)!;
        Assert.True((bool)writer.GetType().GetMethod("TryWrite")!.Invoke(writer, [frame])!);
        return frame;
    }

    [Fact]
    public async Task Stop_DrainsAQueuedFrameRatherThanLeavingItsBufferRented()
    {
        var detector = new VisualEventDetector();
        var frame = QueueFrame(detector, 64);

        Assert.True(
            await CompletesWithin(detector.Stop, TimeSpan.FromSeconds(5)),
            "Stop() blocked with a frame queued");

        Assert.Empty((byte[])FrameDataBuffer.GetValue(frame)!);
    }

    [Fact]
    public void Stop_DisposesTheCancellationTokenSource()
    {
        var detector = new VisualEventDetector();
        var cts = new CancellationTokenSource();
        Field("_cts").SetValue(detector, cts);

        detector.Stop();

        Assert.Throws<ObjectDisposedException>(() => cts.Token);

        detector.Stop();
    }
}
