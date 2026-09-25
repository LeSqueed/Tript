// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text;
using Xunit;

namespace Tript.Obs.IntegrationTests;

public sealed class ObsLifecycleTests
{
    [SkippableFact]
    public void Startup_MakesTheContextCurrent()
    {
        Assert.False(ObsRuntime.IsInitialized);

        using var session = ObsSession.Start();

        Assert.True(ObsRuntime.IsInitialized);
        Assert.Same(session.Runtime, ObsRuntime.Current);
    }

    [SkippableFact]
    public void Shutdown_LeavesNoContextBehind()
    {
        using (ObsSession.Start())
        {
        }

        Assert.False(ObsRuntime.IsInitialized);
        Assert.Null(ObsRuntime.Current);
    }

    [SkippableFact]
    public void ASecondContext_IsRefusedWhileOneIsRunning()
    {
        using var session = ObsSession.Start();

        Assert.Throws<InvalidOperationException>(() => ObsRuntime.Start(new ObsStartupOptions()));
    }

    [SkippableFact]
    public void DisposingTwice_ShutsDownOnce()
    {
        var session = ObsSession.Start();
        session.Dispose();
        session.Dispose();

        Assert.False(ObsRuntime.IsInitialized);
    }

    [SkippableFact]
    public void ADisposedRuntime_ThrowsRatherThanCallingIntoADeadContext()
    {
        var session = ObsSession.Start();
        var runtime = session.Runtime;
        session.Dispose();

        Assert.Throws<ObjectDisposedException>(() => runtime.ResetAudio(new ObsAudioSettings()));
        Assert.Throws<ObjectDisposedException>(() => runtime.EnumerateInputTypes());
    }

    [SkippableFact]
    public void TheStartupBanner_ArrivesThroughTheInstalledHandler()
    {
        using var session = ObsSession.Start();

        Assert.Contains(session.Messages, entry => entry.Message.StartsWith("CPU Name:", StringComparison.Ordinal));
        Assert.Contains(session.Messages, entry => entry.Level == ObsLogLevel.Info);
    }

    [SkippableFact]
    public void TheLocale_RoundTripsByteIdentically()
    {
        const string locale = "zh-Hàn \u2014 Ω \u2014 🎮";

        using var session = ObsSession.Start();
        session.Runtime.Locale = locale;

        Assert.Equal(Encoding.UTF8.GetBytes(locale), Encoding.UTF8.GetBytes(session.Runtime.Locale));
    }

    [SkippableFact]
    public void AnAddedDataPath_ResolvesFilesAndUnknownNamesReturnNull()
    {
        using var session = ObsSession.Start();

        var coreData = ObsTestEnvironment.CoreDataPath;
        if (coreData is null)
            throw new Xunit.SkipException("No OBS core data dir found; nothing to resolve data files from.");

        const string probeFile = "license/gplv2.txt";
        if (!File.Exists(Path.Combine(coreData, probeFile)))
            throw new Xunit.SkipException(
                $"This machine's OBS core data dir ({coreData}) does not ship {probeFile}; there is no file to resolve.");

        session.Runtime.AddDataPath(coreData);

        try
        {
            var found = session.Runtime.FindDataFile(probeFile);

            Assert.NotNull(found);
            Assert.True(File.Exists(found));
            Assert.Null(session.Runtime.FindDataFile("no/such/file.txt"));
        }
        finally
        {
            Assert.True(session.Runtime.RemoveDataPath(coreData));
        }
    }

    [SkippableFact]
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

        session.Runtime.WaitForDestroyQueue();
    }
}
