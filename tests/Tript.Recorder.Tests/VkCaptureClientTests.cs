// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Recorder.Tests;

public sealed class VkCaptureClientTests
{
    private const int GamePid = 4242;

    [Theory]
    [InlineData("/home/me/.steam/steam/steamapps/common/Proton 10.0/files/bin/wine64-preloader")]
    [InlineData("/usr/lib/wine/wine-preloader")]
    public void AProtonGame_IsNamedAfterItsWindowsExecutable(string loader)
    {
        var files = new GameProcessFiles { ExecutableLink = loader, CommandName = "Overwatch.exe" };

        Assert.Equal("Overwatch.exe", VkCaptureClient.NameOf(files, GamePid));
    }

    [Fact]
    public void ANativeGame_IsNamedAfterItsExecutable()
    {
        var files = new GameProcessFiles { ExecutableLink = "/opt/games/dota2/game/bin/linuxsteamrt64/dota2" };

        Assert.Equal("dota2", VkCaptureClient.NameOf(files, GamePid));
    }

    [Fact]
    public void ANameChosenInTheLaunchOptions_WinsOverTheExecutable()
    {
        var files = new GameProcessFiles
        {
            ExecutableLink = "/usr/bin/wine64-preloader",
            CommandName = "Overwatch.exe",
            Environment = { ["OBS_VKCAPTURE_NAME"] = "overwatch" }
        };

        Assert.Equal("overwatch", VkCaptureClient.NameOf(files, GamePid));
    }

    [Fact]
    public void AProcessThatHasExited_HasNoName()
    {
        Assert.Null(VkCaptureClient.NameOf(new GameProcessFiles(), GamePid));
    }

    [Theory]
    [InlineData("1", null, true)]
    [InlineData(null, null, false)]
    [InlineData("0", null, false)]
    [InlineData("1", "1", false)]
    public void TheCaptureLayer_IsLoadedOnlyWhenTheLaunchEnablesIt(string? enable, string? disable, bool loaded)
    {
        var files = new GameProcessFiles { ExecutableLink = "/usr/bin/wine64-preloader" };
        if (enable is not null)
            files.Environment["OBS_VKCAPTURE"] = enable;
        if (disable is not null)
            files.Environment["DISABLE_OBS_VKCAPTURE"] = disable;

        Assert.Equal(loaded, VkCaptureClient.IsLoadedInto(files, GamePid));
    }

    [Fact]
    public void AProcessWhoseDetailsCannotBeRead_LeavesTheCaptureLayerUnknown()
    {
        Assert.Null(VkCaptureClient.IsLoadedInto(new GameProcessFiles(), GamePid));
    }

    private sealed class GameProcessFiles : IProcessFiles
    {
        public string? ExecutableLink { get; init; }

        public string? CommandName { get; init; }

        public Dictionary<string, string> Environment { get; } = [];

        public string? ReadCommandLine(int processId) => null;

        public string? ReadExecutableLink(int processId) => processId == GamePid ? ExecutableLink : null;

        public string? ReadEnvironmentVariable(int processId, string name) =>
            processId == GamePid && Environment.TryGetValue(name, out var value) ? value : null;

        public string? ReadCommandName(int processId) => processId == GamePid ? CommandName : null;
    }
}
