// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using Xunit;

namespace Tript.Media.Tests;

public sealed class FfmpegStallTests
{
    // Run used to wait on ffmpeg with no deadline at all, so a wedged encode blocked the clip queue
    // forever and nothing could get it back. It now stops an ffmpeg that has gone silent.
    [Fact]
    public void AnFfmpegThatStopsProducingOutputIsStoppedInsteadOfBlockingForever()
    {
        var stub = CreateSilentStub();
        var request = new ClipRequest
        {
            SourcePath = "unused.mp4",
            Regions = [],
            Mode = ClipMode.Combine,
            OutputPath = Path.Combine(MediaTestFixture.ScratchRoot, "unused-out.mp4"),
        };

        var clock = Stopwatch.StartNew();
        var failure = Assert.Throws<ClipEncodeException>(() =>
            FfmpegRunner.Run(stub, ["-i", "x"], request, "stall-test", TimeSpan.FromSeconds(2)));
        clock.Stop();

        Assert.Contains("made no progress", failure.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15),
            $"the stalled run took {clock.Elapsed.TotalSeconds:0.0}s; the stub would have run for 30s");
    }

    // Stands in for an ffmpeg that is alive but wedged: it runs for 30s and prints nothing.
    private static string CreateSilentStub()
    {
        var name = $"silent-{Guid.NewGuid():N}";
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(MediaTestFixture.ScratchRoot, name + ".cmd");
            File.WriteAllText(cmd, "@echo off\r\nping -n 30 127.0.0.1 >nul\r\nexit /b 0\r\n");
            return cmd;
        }

        var script = Path.Combine(MediaTestFixture.ScratchRoot, name + ".sh");
        File.WriteAllText(script, "#!/bin/sh\nsleep 30\nexit 0\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }
}
