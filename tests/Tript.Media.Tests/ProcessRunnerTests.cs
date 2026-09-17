// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Media.Tests;

public sealed class ProcessRunnerTests
{
    private static readonly TimeSpan NoHang = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task ProbeRun_ReturnsPromptly_WhenTheChildFloodsStderr()
    {
        var source = MediaTestFixture.CreateSdrSource("probe-chatty.mp4", durationSeconds: 8, audioTracks: 2);

        var arguments = new List<string>
        {
            "-v", "trace", "-show_entries", "format=duration", "-of", "json", source,
        };

        var run = Task.Run(() => MediaProbe.Run(MediaTestFixture.Binaries.Ffprobe, arguments));
        await Finishes(run, "MediaProbe.Run deadlocked: both pipes must be drained concurrently with the wait");

        var (stdout, stderr, exitCode) = await run;
        Assert.Equal(0, exitCode);
        Assert.Contains("duration", stdout, StringComparison.Ordinal);
        Assert.True(stderr.Length > 128 * 1024,
            $"the fixture must actually flood stderr; got {stderr.Length} bytes");
    }

    [Fact]
    public async Task ProbeRun_KillsAChildThatOverrunsTheTimeout()
    {
        var arguments = new List<string>
        {
            "-nostdin", "-loglevel", "error",
            "-re", "-f", "lavfi", "-i", "testsrc=size=64x48:rate=1",
            "-t", "3600", "-f", "null", "-",
        };

        var run = Task.Run(() => MediaProbe.Run(
            MediaTestFixture.Binaries.Ffmpeg, arguments, TimeSpan.FromSeconds(1)));
        await Finishes(run, "an overrunning child must be killed at the timeout");

        var (_, stderr, exitCode) = await run;
        Assert.Equal(-1, exitCode);
        Assert.Contains("did not exit", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void LowerPriority_RunsTheChildBelowNormal()
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = MediaTestFixture.Binaries.Ffmpeg,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            }
        };
        foreach (var argument in new[]
                 {
                     "-nostdin", "-loglevel", "error", "-re", "-f", "lavfi", "-i", "testsrc=size=64x48:rate=1",
                     "-t", "3600", "-f", "null", "-",
                 })
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        Assert.True(process.Start());
        try
        {
            ProcessPipes.LowerPriority(process);
            process.Refresh();

            Assert.Equal(System.Diagnostics.ProcessPriorityClass.BelowNormal, process.PriorityClass);
        }
        finally
        {
            ProcessPipes.KillQuietly(process);
        }
    }

    [Fact]
    public async Task CreateClips_OverwritesAnExistingOutput_WithoutWaitingForAnAnswer()
    {
        var source = MediaTestFixture.CreateSdrSource("overwrite-source.mp4", durationSeconds: 4);
        var outputDirectory = Path.Combine(MediaTestFixture.ScratchRoot, "clips-overwrite");
        Directory.CreateDirectory(outputDirectory);

        var request = new ClipRequest
        {
            SourcePath = source,
            Regions = [ClipRegion.FromSeconds(0.5, 2.5)],
            Mode = ClipMode.Separate,
            OutputPath = outputDirectory,
        };

        var engine = new ClipEngine(MediaTestFixture.Binaries.Ffmpeg,
            new MediaProbe(MediaTestFixture.Binaries.Ffprobe));

        var first = engine.CreateClips(request);
        var clip = Assert.Single(first);

        File.WriteAllText(clip, "leftover from a run that did not finish");

        var second = Task.Run(() => engine.CreateClips(request));
        await Finishes(second, "ffmpeg blocked on an overwrite prompt for an output that already existed");

        Assert.Equal(clip, Assert.Single(await second));
        Assert.Equal("h264", MediaTestFixture.ProbeValue(
            MediaTestFixture.Binaries.Ffprobe, clip, "v:0", "codec_name"));
    }

    [Fact]
    public void CreateClips_SurvivesAProgressSinkThatThrows()
    {
        var source = MediaTestFixture.CreateSdrSource("progress-source.mp4", durationSeconds: 3);
        var outputDirectory = Path.Combine(MediaTestFixture.ScratchRoot, "clips-progress");

        var calls = 0;
        var engine = new ClipEngine(MediaTestFixture.Binaries.Ffmpeg,
            new MediaProbe(MediaTestFixture.Binaries.Ffprobe));

        var paths = engine.CreateClips(new ClipRequest
        {
            SourcePath = source,
            Regions = [ClipRegion.FromSeconds(0.5, 2.0)],
            Mode = ClipMode.Separate,
            OutputPath = outputDirectory,
            Progress = _ =>
            {
                Interlocked.Increment(ref calls);
                throw new InvalidOperationException("the client this was streaming to is gone");
            },
        });

        var clip = Assert.Single(paths);
        Assert.True(File.Exists(clip));

        Assert.True(calls > 1, $"the progress sink must still be called; got {calls} calls");
    }

    [Fact]
    public void RunBounded_Timeout_LeavesNoUnobservedTaskException()
    {
        var unobserved = new List<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            foreach (var inner in e.Exception.InnerExceptions)
            {
                if (inner is IOException or ObjectDisposedException)
                    lock (unobserved) unobserved.Add(inner);
            }
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            for (var i = 0; i < 3; i++)
            {
                var outcome = FfmpegRunner.RunBounded(MediaTestFixture.Binaries.Ffmpeg,
                    ["-nostdin", "-loglevel", "error", "-re", "-f", "lavfi",
                        "-i", "testsrc=size=64x48:rate=1", "-t", "3600", "-f", "null", "-"],
                    TimeSpan.FromSeconds(1));
                Assert.False(outcome.Completed);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        lock (unobserved)
            Assert.Empty(unobserved);
    }

    private static async Task Finishes(Task task, string because)
    {
        var finished = await Task.WhenAny(task, Task.Delay(NoHang));
        Assert.True(ReferenceEquals(finished, task), because);
    }
}
