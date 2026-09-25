// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.TestSupport;
using Xunit;

namespace Tript.Recorder.Tests;

public sealed class LinuxProcessIdentityTests
{
    private const string OverwatchZ = @"Z:\home\u\.local\share\Steam\steamapps\common\Overwatch\Overwatch.exe";
    private const string OverwatchLinux = "/home/u/.local/share/Steam/steamapps/common/Overwatch/Overwatch.exe";
    private const string ProtonBin = "/home/u/.local/share/Steam/steamapps/common/Proton 10.0/files/bin";

    [Fact]
    public void ProtonGameIsNamedAfterItsWindowsExecutableAndMappedFromDriveZ()
    {
        var files = new FakeProcessFiles(Line(OverwatchZ), $"{ProtonBin}/wine64-preloader");

        var identity = LinuxProcessIdentity.Read(files, 7);

        Assert.Equal(new ProcessIdentity("Overwatch.exe", OverwatchLinux), identity);
    }

    [Fact]
    public void WineLoaderArgumentsBeforeTheExecutableAreSkipped()
    {
        var files = new FakeProcessFiles(
            Line($"{ProtonBin}/wine64-preloader", $"{ProtonBin}/wine64", OverwatchZ, "--tank-mode"),
            $"{ProtonBin}/wine64-preloader");

        Assert.Equal(new ProcessIdentity("Overwatch.exe", OverwatchLinux), LinuxProcessIdentity.Read(files, 7));
    }

    [Fact]
    public void UnixPathToAWindowsExecutableIsKeptAsItsOwnPath()
    {
        var files = new FakeProcessFiles(Line(OverwatchLinux), "/usr/bin/wine-preloader");

        Assert.Equal(new ProcessIdentity("Overwatch.exe", OverwatchLinux), LinuxProcessIdentity.Read(files, 7));
    }

    [Fact]
    public void PrefixDriveWithoutAKnownPrefixKeepsTheNameButNoPath()
    {
        var files = new FakeProcessFiles(Line(@"C:\Games\Solitaire\solitaire.exe"), "/usr/bin/wine64-preloader");

        Assert.Equal(new ProcessIdentity("solitaire.exe", null), LinuxProcessIdentity.Read(files, 7));
    }

    [Fact]
    public void PrefixDriveResolvesThroughTheProcessWinePrefix()
    {
        var files = new FakeProcessFiles(Line(@"C:\Games\Solitaire\solitaire.exe"), "/usr/bin/wine64-preloader")
        {
            WinePrefix = "/home/u/.local/share/Steam/steamapps/compatdata/42/pfx/",
        };

        Assert.Equal(
            new ProcessIdentity("solitaire.exe",
                "/home/u/.local/share/Steam/steamapps/compatdata/42/pfx/dosdevices/c:/Games/Solitaire/solitaire.exe"),
            LinuxProcessIdentity.Read(files, 7));
    }

    [Fact]
    public void DriveZNeverConsultsTheProcessEnvironment()
    {
        var files = new FakeProcessFiles(Line(OverwatchZ), null) { WinePrefix = "/elsewhere" };

        LinuxProcessIdentity.Read(files, 7);

        Assert.False(files.EnvironmentRead);
    }

    [Theory]
    [InlineData(@"Z:\games\My Game\Game.exe -dx12 -windowed", "Game.exe", "/games/My Game/Game.exe")]
    [InlineData("\"Z:\\games\\My Game\\Game.exe\" -dx12", "Game.exe", "/games/My Game/Game.exe")]
    [InlineData(@"z:/games/game.EXE", "game.EXE", "/games/game.EXE")]
    public void ExecutableCarryingItsArgumentsInOneEntryIsSplitAtTheExtension(
        string commandLine, string executable, string path)
    {
        var files = new FakeProcessFiles(Line(commandLine), null);

        Assert.Equal(new ProcessIdentity(executable, path), LinuxProcessIdentity.Read(files, 7));
    }

    [Fact]
    public void LaunchWrapperNamingTheGameInItsArgumentsIsNotTheGame()
    {
        var files = new FakeProcessFiles(
            Line("python3", "/home/u/.local/share/Steam/steamapps/common/Proton 10.0/proton", "waitforexitandrun", OverwatchLinux),
            "/usr/bin/python3.13");

        Assert.Equal(new ProcessIdentity("python3.13", "/usr/bin/python3.13"), LinuxProcessIdentity.Read(files, 7));
    }

    [Fact]
    public void ProtonSteamShimIsNotMistakenForTheGameItStarts()
    {
        var files = new FakeProcessFiles(Line(@"C:\windows\system32\steam.exe", OverwatchZ), "/usr/bin/wine64-preloader");

        Assert.Equal("steam.exe", LinuxProcessIdentity.Read(files, 7)?.Executable);
    }

    [Fact]
    public void NativeGameIsIdentifiedByItsExecutableLink()
    {
        var files = new FakeProcessFiles(Line("./GravityCircuit.x86_64", "-screen-fullscreen"),
            "/games/Gravity Circuit/GravityCircuit.x86_64");

        Assert.Equal(
            new ProcessIdentity("GravityCircuit.x86_64", "/games/Gravity Circuit/GravityCircuit.x86_64"),
            LinuxProcessIdentity.Read(files, 7));
    }

    [Fact]
    public void ReplacedNativeExecutableDropsTheDeletedMarker()
    {
        var files = new FakeProcessFiles(Line("game"), "/games/game (deleted)");

        Assert.Equal(new ProcessIdentity("game", "/games/game"), LinuxProcessIdentity.Read(files, 7));
    }

    [Fact]
    public void WineLoaderWithoutAWindowsCommandLineHasNoIdentity()
    {
        var files = new FakeProcessFiles(Line($"{ProtonBin}/wine64-preloader"), $"{ProtonBin}/wine64-preloader");

        Assert.Null(LinuxProcessIdentity.Read(files, 7));
    }

    [Fact]
    public void UnreadableProcessHasNoIdentity()
        => Assert.Null(LinuxProcessIdentity.Read(new FakeProcessFiles(null, null), 7));

    [Theory]
    [InlineData("a\0b\0", new[] { "a", "b" })]
    [InlineData("a\0\0b", new[] { "a", "", "b" })]
    [InlineData("single", new[] { "single" })]
    [InlineData("", new string[0])]
    public void CommandLineSplitsOnNulAndDropsTheTerminator(string raw, string[] expected)
        => Assert.Equal(expected, LinuxProcessIdentity.SplitCommandLine(raw));

    [Theory]
    [InlineData(@"D:\Games\x.exe", null)]
    [InlineData(@"\\server\share\x.exe", null)]
    [InlineData("x.exe", null)]
    [InlineData(@"Z:\x.exe", "/x.exe")]
    public void OnlyDriveZAndUnixPathsMapWithoutAPrefix(string windowsPath, string? expected)
        => Assert.Equal(expected, LinuxProcessIdentity.ToLinuxPath(windowsPath, null));

    [Theory]
    [InlineData(".exe")]
    [InlineData(@"C:\games\")]
    [InlineData("/usr/bin/game")]
    public void ArgumentWithoutAWindowsExecutableIsRejected(string argument)
        => Assert.False(LinuxProcessIdentity.TryFindWindowsExecutable([argument], out _));

    [LinuxFact]
    public void ProcFilesIdentifyTheRunningTestProcessByItsExecutable()
    {
        var identity = LinuxProcessIdentity.Read(new ProcProcessFiles(), Environment.ProcessId);

        Assert.NotNull(identity);
        Assert.Equal(Environment.ProcessPath, identity.ExecutablePath);
        Assert.Equal(Path.GetFileName(Environment.ProcessPath), identity.Executable);
    }

    [LinuxFact]
    public void ProcFilesReadTheProcessEnvironment()
    {
        var home = Environment.GetEnvironmentVariable("HOME");

        Assert.Equal(home, new ProcProcessFiles().ReadEnvironmentVariable(Environment.ProcessId, "HOME"));
    }

    internal static string Line(params string[] arguments) => string.Join('\0', arguments) + '\0';
}

internal sealed class FakeProcessFiles(string? commandLine, string? executableLink) : IProcessFiles
{
    public string? WinePrefix { get; init; }

    public bool EnvironmentRead { get; private set; }

    public string? ReadCommandLine(int processId) => commandLine;

    public string? ReadExecutableLink(int processId) => executableLink;

    public string? ReadEnvironmentVariable(int processId, string name)
    {
        EnvironmentRead = true;
        return name == "WINEPREFIX" ? WinePrefix : null;
    }
}
