// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text;
using Xunit;
using Xunit.Sdk;

namespace Tript.Obs.IntegrationTests;

// Bringing a real OBS context up and down. Each test owns its own context from first line to last,
// because there is only one per process and sharing it between tests would make the order they run
// in part of what is being tested.
public sealed class ObsLifecycleTests
{
    [Fact]
    public void Startup_MakesTheContextCurrent()
    {
        Assert.False(ObsRuntime.IsInitialized);

        using var session = ObsSession.Start();

        Assert.True(ObsRuntime.IsInitialized);
        Assert.Same(session.Runtime, ObsRuntime.Current);
    }

    [Fact]
    public void Shutdown_LeavesNoContextBehind()
    {
        using (ObsSession.Start())
        {
        }

        Assert.False(ObsRuntime.IsInitialized);
        Assert.Null(ObsRuntime.Current);
    }

    [Fact]
    public void ASecondContext_IsRefusedWhileOneIsRunning()
    {
        using var session = ObsSession.Start();

        Assert.Throws<InvalidOperationException>(() => ObsRuntime.Start(new ObsStartupOptions()));
    }

    [Fact]
    public void DisposingTwice_ShutsDownOnce()
    {
        var session = ObsSession.Start();
        session.Dispose();
        session.Dispose();

        Assert.False(ObsRuntime.IsInitialized);
    }

    [Fact]
    public void ADisposedRuntime_ThrowsRatherThanCallingIntoADeadContext()
    {
        var session = ObsSession.Start();
        var runtime = session.Runtime;
        session.Dispose();

        Assert.Throws<ObjectDisposedException>(() => runtime.ResetAudio(new ObsAudioSettings()));
        Assert.Throws<ObjectDisposedException>(() => runtime.EnumerateInputTypes());
    }

    [Fact]
    public void TheStartupBanner_ArrivesThroughTheInstalledHandler()
    {
        using var session = ObsSession.Start();

        // libobs prints its own environment survey during startup. Its arrival proves the handler
        // was installed before startup and that formatted output crosses the boundary intact.
        Assert.Contains(session.Messages, entry => entry.Message.StartsWith("CPU Name:", StringComparison.Ordinal));
        Assert.Contains(session.Messages, entry => entry.Level == ObsLogLevel.Info);
    }

    // The locale is the one string the core stores and hands straight back, which makes it the
    // cleanest byte-level round trip through libobs's own storage.
    [Fact]
    public void TheLocale_RoundTripsByteIdentically()
    {
        const string locale = "zh-Hàn — Ω — 🎮";

        using var session = ObsSession.Start();
        session.Runtime.Locale = locale;

        Assert.Equal(Encoding.UTF8.GetBytes(locale), Encoding.UTF8.GetBytes(session.Runtime.Locale));
    }

    [Fact]
    public void AnAddedDataPath_ResolvesFilesAndUnknownNamesReturnNull()
    {
        using var session = ObsSession.Start();

        // The core data dir is discovered with the runtime; a stripped install may not have it.
        var coreData = ObsTestEnvironment.CoreDataPath;
        if (coreData is null)
            throw SkipException.ForSkip("No OBS core data dir found; nothing to resolve data files from.");

        session.Runtime.AddDataPath(coreData);

        try
        {
            var found = session.Runtime.FindDataFile("license/gplv2.txt");

            Assert.NotNull(found);
            Assert.True(File.Exists(found));
            Assert.Null(session.Runtime.FindDataFile("no/such/file.txt"));
        }
        finally
        {
            // Data paths outlive obs_shutdown; leaving one behind would make the next test's leak
            // accounting start from a different floor.
            Assert.True(session.Runtime.RemoveDataPath(coreData));
        }
    }

    // The check that matters most here: a startup and shutdown cycle must give libobs's
    // allocator back everything it took. Measured after a warm-up cycle, because the first one
    // legitimately retains process-wide state — the point is that repetition does not accumulate.
    [Fact]
    public void RepeatedStartupAndShutdown_LeavesNoLiveAllocations()
    {
        RunCycle();
        var baseline = ObsRuntime.LiveAllocationCount;

        for (var i = 0; i < 4; i++)
        {
            RunCycle();
            Assert.True(ObsRuntime.LiveAllocationCount <= baseline,
                $"Cycle {i + 1} left {ObsRuntime.LiveAllocationCount - baseline} extra live bmem allocation(s).");
        }
    }

    private static void RunCycle()
    {
        using var session = ObsSession.Start();

        session.ResetVideoOrThrow(new ObsVideoSettings
        {
            BaseWidth = 1920,
            BaseHeight = 1080,
            OutputWidth = 1280,
            OutputHeight = 720
        });

        Assert.True(session.Runtime.ResetAudio(new ObsAudioSettings()));

        // Drains whatever release scheduled. Its return value says whether anything was waiting,
        // not whether the drain worked, so there is nothing to assert on.
        session.Runtime.WaitForDestroyQueue();
    }
}
