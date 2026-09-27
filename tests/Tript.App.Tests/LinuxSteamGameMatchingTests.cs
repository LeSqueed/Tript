// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Core;
using Tript.Recorder;
using Tript.TestSupport;
using Xunit;

namespace Tript.App.Tests;

public sealed class LinuxSteamGameMatchingTests : IDisposable
{
    private const string OverwatchId = "5JWDDE307Z5127JK7KM4YCB1XW";

    private readonly string _home = FilePaths.ResolveLinks(Directory.CreateTempSubdirectory("tript-linux-steam-").FullName);

    public void Dispose() => Directory.Delete(_home, true);

    [LinuxFact]
    public void OverwatchRunningUnderProtonIsDetectedAgainstItsResolvedPath()
    {
        var executable = InstallOverwatch();
        var files = new ProtonProcessFiles("Z:" + executable.Replace('/', '\\'));
        using var detector = new ProcessNameGameDetector(
            [new GameDetectionTarget(OverwatchId, "Overwatch.exe", executable)],
            candidates =>
            {
                var identity = LinuxProcessIdentity.Read(files, 4242)!;
                return candidates.Contains(ProcessNameGameDetector.NormalizeProcessName(identity.Executable))
                    ? [new ProcessSnapshot(4242, identity.Executable, identity.ExecutablePath)]
                    : [];
            },
            TimeSpan.FromHours(1));
        DetectedGameProcess? started = null;
        detector.GameStarted += process => started = process;

        detector.PollOnce();
        detector.WaitForCallbacks();

        Assert.NotNull(started);
        Assert.Equal(OverwatchId, started.GameId);
        Assert.Equal(executable, started.ExecutablePath);
    }

    private string InstallOverwatch()
    {
        var steamapps = Directory.CreateDirectory(Path.Combine(_home, ".local", "share", "Steam", "steamapps")).FullName;
        File.WriteAllText(Path.Combine(steamapps, "appmanifest_2357570.acf"),
            "\"AppState\" { \"appid\" \"2357570\" \"name\" \"Overwatch\" \"installdir\" \"Overwatch\" }");
        var install = Directory.CreateDirectory(Path.Combine(steamapps, "common", "Overwatch")).FullName;
        var executable = Path.Combine(install, "Overwatch.exe");
        File.WriteAllText(executable, "not a real PE; only the path matters");
        return executable;
    }

    private sealed class ProtonProcessFiles(string windowsExecutable) : IProcessFiles
    {
        public string? ReadCommandLine(int processId) => windowsExecutable + "\0";

        public string? ReadExecutableLink(int processId) => "/opt/proton/files/bin/wine64-preloader";

        public string? ReadEnvironmentVariable(int processId, string name) => null;

        public string? ReadCommandName(int processId) => null;
    }
}
