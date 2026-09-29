// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Media;
using Xunit;

namespace Tript.Media.Tests;

public sealed class ProcessPipesStarvationTests
{
    [Fact]
    public async Task AProbeStillReadsItsOutput_WhileEveryPoolThreadIsBusy()
    {
        var path = MediaTestFixture.CreateSdrSource($"starved-{Guid.NewGuid():N}.mp4", durationSeconds: 2);
        ThreadPool.GetMinThreads(out var workers, out _);
        using var release = new ManualResetEventSlim(false);
        var blockers = Enumerable.Range(0, workers * 2)
            .Select(_ => Task.Run(() => release.Wait(TimeSpan.FromSeconds(30))))
            .ToArray();

        try
        {
            var info = new MediaProbe(MediaTestFixture.Binaries.Ffprobe).Probe(path);

            Assert.InRange(info.DurationSeconds, 1.5, 2.5);
        }
        finally
        {
            release.Set();
            await Task.WhenAll(blockers).WaitAsync(TimeSpan.FromSeconds(30));
        }
    }
}
