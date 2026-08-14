// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System;
using System.Threading.Tasks;
using Tript.Detection;
using Xunit;

namespace Tript.Detection.Tests;

// Start() needs a real ONNX model and a live OBS subscription, so the only path reachable from a
// unit test is the never-started one through Stop() — the branch the Task -> Thread swap
// introduced (_detectionThread == null).
public class DetectionThreadLifecycleTests
{
    private static async Task<bool> CompletesWithin(Action action, TimeSpan timeout)
    {
        var work = Task.Run(action);
        return await Task.WhenAny(work, Task.Delay(timeout)) == work;
    }

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
}
