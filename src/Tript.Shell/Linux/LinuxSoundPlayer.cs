// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.ComponentModel;
using System.Diagnostics;
using Serilog;

namespace Tript.Shell.Linux;

internal sealed class LinuxSoundPlayer
{
    internal static readonly string[] PlayerNames = ["pw-play", "paplay"];

    private readonly IReadOnlyList<string> _players;
    private readonly Func<string, string, Process?> _start;

    internal LinuxSoundPlayer(IReadOnlyList<string> players, Func<string, string, Process?>? start = null)
    {
        _players = players;
        _start = start ?? StartPlayer;
    }

    internal static LinuxSoundPlayer ForThisSystem() =>
        new(FindPlayers(Environment.GetEnvironmentVariable("PATH"), File.Exists));

    internal bool Available => _players.Count > 0;

    internal static IReadOnlyList<string> FindPlayers(string? searchPath, Func<string, bool> fileExists)
    {
        var directories = (searchPath ?? string.Empty)
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var found = new List<string>();
        foreach (var name in PlayerNames)
        {
            var path = directories.Select(directory => Path.Combine(directory, name)).FirstOrDefault(fileExists);
            if (path is not null)
                found.Add(path);
        }

        return found;
    }

    internal void Play(string soundFile)
    {
        if (Available && File.Exists(soundFile))
            ThreadPool.QueueUserWorkItem(_ => PlayWith(0, soundFile));
    }

    internal void PlayWith(int playerIndex, string soundFile)
    {
        for (var index = playerIndex; index < _players.Count; index++)
        {
            Process? process;
            try
            {
                process = _start(_players[index], soundFile);
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                Log.Debug(exception, "Tript.Shell: {Player} could not be started", _players[index]);
                continue;
            }

            if (process is null)
                continue;

            var next = index + 1;
            process.Exited += (_, _) =>
            {
                var failed = process.ExitCode != 0;
                process.Dispose();
                if (failed)
                    PlayWith(next, soundFile);
            };
            process.EnableRaisingEvents = true;
            return;
        }
    }

    private static Process? StartPlayer(string player, string soundFile)
    {
        var start = new ProcessStartInfo(player) { UseShellExecute = false };
        start.ArgumentList.Add(soundFile);
        return Process.Start(start);
    }
}
