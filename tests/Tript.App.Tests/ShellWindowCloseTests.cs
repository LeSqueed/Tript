// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;
using Decision = Tript.Shell.Program.WindowCloseDecision;

namespace Tript.App.Tests;

public sealed class ShellWindowCloseTests
{
    private static Decision Decide(bool exitRequested = false, bool hideToTray = false,
        bool trayReachable = true, bool recording = false) =>
        Tript.Shell.Program.DecideWindowClose(exitRequested, hideToTray, () => trayReachable, recording);

    [Fact]
    public void HidingToTray_WhileRecording_KeepsTheRecordingRunning()
    {
        Assert.Equal(Decision.HideToTray, Decide(hideToTray: true, recording: true));
    }

    [Fact]
    public void HidingToTray_WithoutAReachableTray_MinimizesAndStillKeepsRecording()
    {
        Assert.Equal(Decision.MinimizeToTaskbar,
            Decide(hideToTray: true, trayReachable: false, recording: true));
    }

    [Fact]
    public void Exiting_WhileRecording_StopsAndSavesBeforeClosing()
    {
        Assert.Equal(Decision.StopRecordingThenExit, Decide(recording: true));
    }

    [Fact]
    public void Exiting_WhenIdle_JustCloses()
    {
        Assert.Equal(Decision.Close, Decide());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnExitAlreadyInProgress_LetsTheWindowClose(bool recording)
    {
        Assert.Equal(Decision.Close, Decide(exitRequested: true, hideToTray: true, recording: recording));
    }

    [Fact]
    public void TheTrayIsOnlyProbedWhenHidingIsWanted()
    {
        var probed = false;
        Tript.Shell.Program.DecideWindowClose(false, hideToTray: false, () => probed = true, recording: true);
        Assert.False(probed);
    }
}
