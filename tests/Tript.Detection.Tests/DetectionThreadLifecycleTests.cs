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

// Start() needs a real ONNX model and a live OBS subscription, so the only path reachable from a
// unit test is the never-started one through Stop() — the branch the Task -> Thread swap
// introduced (_detectionThread == null).
public class DetectionThreadLifecycleTests
{
    // Awaits the completed task so a faulted one rethrows here: WhenAny alone never throws, which
    // let a Stop() that threw count as "completed within the timeout".
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

    // ---- teardown against a frame callback that is still running ----

    private static readonly Type FrameDataType =
        typeof(VisualEventDetector).GetNestedType("FrameData", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("VisualEventDetector.FrameData not found");

    private static readonly FieldInfo FrameDataBuffer =
        FrameDataType.GetField("_buffer", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("FrameData._buffer not found");

    private static FieldInfo Field(string name) =>
        typeof(VisualEventDetector).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"VisualEventDetector.{name} not found");

    // Queues a frame the way OnFrame does: a pooled buffer handed to the channel.
    private static object QueueFrame(VisualEventDetector detector, int size)
    {
        var frame = Activator.CreateInstance(FrameDataType, nonPublic: true)!;
        FrameDataBuffer.SetValue(frame, ArrayPool<byte>.Shared.Rent(size));

        var queue = Field("_frameQueue").GetValue(detector)!;
        var writer = queue.GetType().GetProperty("Writer")!.GetValue(queue)!;
        Assert.True((bool)writer.GetType().GetMethod("TryWrite")!.Invoke(writer, [frame])!);
        return frame;
    }

    // Disposing the OBS subscription does not wait for a callback already in flight: it finishes
    // its copy and queues the ~8 MB buffer it rented afterwards. A teardown that drains before that
    // write returns and leaves the buffer in a channel nothing reads until the next Start.
    [Fact]
    public async Task Stop_DrainsAFrameQueuedByACallbackThatWasStillRunning()
    {
        var detector = new VisualEventDetector();
        Field("_isProcessing").SetValue(detector, 1);

        var stop = Task.Run(detector.Stop);
        Assert.False(
            await Completes(stop, TimeSpan.FromMilliseconds(300)),
            "Stop() must not finish while a frame callback is still holding a rented buffer");

        var frame = QueueFrame(detector, 64);
        Field("_isProcessing").SetValue(detector, 0);

        Assert.True(
            await Completes(stop, TimeSpan.FromSeconds(5)),
            "Stop() must finish once the callback is done");
        Assert.Empty((byte[])FrameDataBuffer.GetValue(frame)!);
    }

    // The token source outlives every Start/Stop cycle otherwise, and its wait handle is a kernel
    // object per cycle.
    [Fact]
    public void Stop_DisposesTheCancellationTokenSource()
    {
        var detector = new VisualEventDetector();
        var cts = new CancellationTokenSource();
        Field("_cts").SetValue(detector, cts);

        detector.Stop();

        Assert.Throws<ObjectDisposedException>(() => cts.Token);

        // And the field is cleared, so the next Stop() does not cancel a disposed source.
        detector.Stop();
    }
}
