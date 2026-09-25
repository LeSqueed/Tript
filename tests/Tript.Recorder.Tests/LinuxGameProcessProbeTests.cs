// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Recorder.Tests;

public sealed class LinuxGameProcessProbeTests
{
    private const string Library = "/home/u/.local/share/Steam/steamapps/common";
    private const string Hunter = Library + "/Way Of The Hunter";

    private readonly FakeProcessFiles _files = new();
    private readonly List<RunningProcess> _processes = [];
    private IReadOnlyList<string> _roots = [Hunter, Library + "/Overwatch"];
    private Func<string, string> _resolveLinks = path => path;

    private LinuxGameProcessProbe Probe() => new(() => _roots, _files, () => _processes, path => _resolveLinks(path));

    private void Proton(int processId, string windowsPath, long residentBytes)
    {
        _files.CommandLines[processId] = windowsPath + "\0";
        _processes.Add(new RunningProcess(processId, residentBytes, null));
    }

    private void Native(int processId, string path, long residentBytes)
    {
        _files.Executables[processId] = path;
        _processes.Add(new RunningProcess(processId, residentBytes, null));
    }

    [Fact]
    public void AProtonGameRunningFromASteamLibrary_IsProposed()
    {
        Proton(501, @"Z:\home\u\.local\share\Steam\steamapps\common\Way Of The Hunter\WayOfTheHunter.exe", 900_000_000);

        var candidate = Probe().Probe();

        Assert.NotNull(candidate);
        Assert.Equal(501, candidate.ProcessId);
        Assert.Equal("WayOfTheHunter", candidate.Executable);
        Assert.Equal(Hunter + "/WayOfTheHunter.exe", candidate.ExecutablePath);
    }

    [Fact]
    public void ANativeGameRunningFromASteamLibrary_IsProposed()
    {
        Native(610, Library + "/Overwatch/overwatch.x86_64", 1_000_000);

        Assert.Equal(Library + "/Overwatch/overwatch.x86_64", Probe().Probe()?.ExecutablePath);
    }

    [Fact]
    public void AProcessOutsideEverySteamLibrary_IsNotAGame()
    {
        Native(700, "/usr/bin/firefox", 2_000_000_000);
        Proton(701, @"Z:\home\u\.local\share\Steam\steamapps\common\Proton 9.0\files\bin\steam.exe", 50_000_000);

        Assert.Null(Probe().Probe());
    }

    [Fact]
    public void CrashHandlersAndAntiCheatLaunchers_AreNeverProposed()
    {
        Proton(801, @"Z:\home\u\.local\share\Steam\steamapps\common\Way Of The Hunter\UnityCrashHandler64.exe", 900_000_000);
        Proton(802, @"Z:\home\u\.local\share\Steam\steamapps\common\Way Of The Hunter\EasyAntiCheat\EasyAntiCheat_EOS.exe", 900_000_000);

        Assert.Null(Probe().Probe());
    }

    [Fact]
    public void AmongSeveralProcessesOfAGame_TheLargestIsTheGame()
    {
        Proton(901, @"Z:\home\u\.local\share\Steam\steamapps\common\Way Of The Hunter\launcher.exe", 40_000_000);
        Proton(902, @"Z:\home\u\.local\share\Steam\steamapps\common\Way Of The Hunter\WayOfTheHunter.exe", 3_000_000_000);
        Proton(903, @"Z:\home\u\.local\share\Steam\steamapps\common\Way Of The Hunter\tools\updater.exe", 20_000_000);

        Assert.Equal(902, Probe().Probe()?.ProcessId);
    }

    [Fact]
    public void ALibraryReachedThroughASymlink_StillMatchesTheGamesRealPath()
    {
        _roots = ["/home/u/.steam/steam/steamapps/common/Way Of The Hunter"];
        _resolveLinks = path => path.Replace("/home/u/.steam/steam", "/home/u/.local/share/Steam", StringComparison.Ordinal);
        Proton(1001, @"Z:\home\u\.local\share\Steam\steamapps\common\Way Of The Hunter\WayOfTheHunter.exe", 1);

        Assert.Equal(1001, Probe().Probe()?.ProcessId);
    }

    [Fact]
    public void WithNoInstalledGamesKnown_NothingIsProposed()
    {
        _roots = [];
        Proton(1101, @"Z:\home\u\.local\share\Steam\steamapps\common\Way Of The Hunter\WayOfTheHunter.exe", 1);

        Assert.Null(Probe().Probe());
    }

    [Fact]
    public void AGameFolderWhoseNameStartsLikeAnother_IsNotConfusedWithIt()
    {
        _roots = [Library + "/Way"];
        Proton(1201, @"Z:\home\u\.local\share\Steam\steamapps\common\Way Of The Hunter\WayOfTheHunter.exe", 1);

        Assert.Null(Probe().Probe());
    }

    private sealed class FakeProcessFiles : IProcessFiles
    {
        internal Dictionary<int, string> CommandLines { get; } = [];

        internal Dictionary<int, string> Executables { get; } = [];

        public string? ReadCommandLine(int processId) => CommandLines.GetValueOrDefault(processId);

        public string? ReadExecutableLink(int processId) => Executables.GetValueOrDefault(processId);

        public string? ReadEnvironmentVariable(int processId, string name) => null;
    }
}
