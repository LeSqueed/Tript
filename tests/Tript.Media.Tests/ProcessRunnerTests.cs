// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using Xunit;

namespace Tript.Media.Tests;

// The process-plumbing guarantees the media runners rest on: neither runner may park its caller.
// Both sit on live request paths — the library listing probes every file, the clip dialog waits on
// the encode — so a runner that can block indefinitely wedges a worker thread for good. Every wait
// here is bounded, so a regression fails the test instead of hanging the suite.
public sealed class ProcessRunnerTests
{
    private static readonly TimeSpan NoHang = TimeSpan.FromSeconds(60);

    // The deadlock a sequential drain produces: read stdout to EOF, then stderr. stdout only
    // reaches EOF when the child exits, and a child that has filled the stderr pipe nobody is
    // reading cannot exit. Both parked, with no timeout to break it.
    [Fact]
    public async Task ProbeRun_ReturnsPromptly_WhenTheChildFloodsStderr()
    {
        var source = MediaTestFixture.CreateSdrSource("probe-chatty.mp4", durationSeconds: 8, audioTracks: 2);

        // -v trace logs every packet: a couple of hundred kilobytes on this fixture, against a pipe
        // buffer of 64 KiB, while stdout gets only the small JSON at the end.
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

    // A probe that will not exit is killed at the timeout and reported as a failure, rather than
    // holding the listing request that asked for the file's duration.
    [Fact]
    public async Task ProbeRun_KillsAChildThatOverrunsTheTimeout()
    {
        // -re paces a synthetic input at its native rate, so this would run for an hour at nearly
        // no CPU cost — a wedged child, without having to wedge one.
        var arguments = new List<string>
        {
            "-nostdin", "-loglevel", "error",
            "-re", "-f", "lavfi", "-i", "testsrc=size=64x48:rate=1",
            "-t", "3600", "-f", "null", "-",
        };

        var stopwatch = Stopwatch.StartNew();
        var run = Task.Run(() => MediaProbe.Run(
            MediaTestFixture.Binaries.Ffmpeg, arguments, TimeSpan.FromSeconds(1)));
        await Finishes(run, "an overrunning child must be killed at the timeout");
        stopwatch.Stop();

        var (_, stderr, exitCode) = await run;
        Assert.Equal(-1, exitCode);
        Assert.Contains("did not exit", stderr, StringComparison.Ordinal);
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 0.5, 30);
    }

    // Without -y an ffmpeg whose output already exists prompts on stdin and never exits. The clip
    // name is derived from the source and the region, so re-clipping the same region hits exactly
    // that path.
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

        // Stand in for what a killed encode leaves behind: a file at the clip's name that is not a
        // clip. ffmpeg must replace it rather than stop and ask.
        File.WriteAllText(clip, "leftover from a run that did not finish");

        var second = Task.Run(() => engine.CreateClips(request));
        await Finishes(second, "ffmpeg blocked on an overwrite prompt for an output that already existed");

        Assert.Equal(clip, Assert.Single(await second));
        Assert.Equal("h264", MediaTestFixture.ProbeValue(
            MediaTestFixture.Binaries.Ffprobe, clip, "v:0", "codec_name"));
    }

    // Progress arrives on a Process event thread, where an unhandled exception is fatal to the
    // whole host. A sink that throws must cost nothing more than its own messages.
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
        // ffmpeg's own stderr keeps coming after the opening line, so the guard is exercised on the
        // event thread and not only on the caller's.
        Assert.True(calls > 1, $"the progress sink must still be called; got {calls} calls");
    }

    // The timeout path used to abandon its two pending reads to a Process that the enclosing using
    // was already disposing, which surfaces later as an unobserved task exception on an unrelated
    // thread.
    [Fact]
    public void RunBounded_Timeout_LeavesNoUnobservedTaskException()
    {
        var unobserved = new List<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            // Only pipe faults are this test's business; the handler is process-wide.
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
