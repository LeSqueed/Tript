// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Recorder;
using Xunit;

namespace Tript.Recorder.Tests;

public sealed class FullscreenGameDetectorTests
{
    [Theory]
    [InlineData("C:\\Windows\\System32\\explorer.exe")]
    [InlineData("C:\\Windows\\SysWOW64\\something.exe")]
    [InlineData("C:\\Program Files\\WindowsApps\\Example\\game.exe")]
    public void SystemLocationsAreIgnored(string path)
    {
        if (!OperatingSystem.IsWindows())
            return;
        Assert.True(FullscreenGameDetector.IsSystemExecutable(path));
    }

    [Theory]
    [InlineData("C:\\Games\\Overwatch\\Overwatch.exe")]
    [InlineData("D:\\SteamLibrary\\steamapps\\common\\Game\\game.exe")]
    public void UserGameLocationsRemainEligible(string path)
    {
        if (!OperatingSystem.IsWindows())
            return;
        Assert.False(FullscreenGameDetector.IsSystemExecutable(path));
    }

    [Fact]
    public void SystemPathPrefixDoesNotMatchASimilarlyNamedUserFolder()
    {
        if (!OperatingSystem.IsWindows())
            return;
        Assert.False(FullscreenGameDetector.IsUnderDirectory(
            "C:\\WindowsGames\\game.exe", "C:\\Windows"));
    }
}
