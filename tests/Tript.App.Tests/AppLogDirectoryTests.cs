// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.App.Tests;

public sealed class AppLogDirectoryTests
{
    private static Func<string, string?> Environment(string? stateHome) =>
        name => name == "XDG_STATE_HOME" ? stateHome : null;

    [Fact]
    public void DefaultLogDirectory_OnLinux_LivesInTheXdgStateHome()
    {
        var directory = AppLog.DefaultLogDirectory(windows: false, Environment("/home/ana/.state"), "/home/ana",
            () => "/home/ana/.config/Tript");

        Assert.Equal(Path.Combine("/home/ana/.state", "Tript", "logs"), directory);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/state")]
    public void DefaultLogDirectory_OnLinux_DefaultsToLocalStateUnderHome(string? stateHome)
    {
        var directory = AppLog.DefaultLogDirectory(windows: false, Environment(stateHome), "/home/ana",
            () => "/home/ana/.config/Tript");

        Assert.Equal(Path.Combine("/home/ana", ".local", "state", "Tript", "logs"), directory);
    }

    [Fact]
    public void DefaultLogDirectory_OnWindows_StaysBesideTheSettings()
    {
        var directory = AppLog.DefaultLogDirectory(windows: true, Environment("/ignored"), @"C:\Users\ana",
            () => @"C:\Users\ana\AppData\Roaming\Tript");

        Assert.Equal(Path.Combine(@"C:\Users\ana\AppData\Roaming\Tript", "logs"), directory);
    }
}
