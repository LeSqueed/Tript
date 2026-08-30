// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Recorder;

namespace Tript.App;

internal sealed class DetectedGameTracker
{
    private readonly object _gate = new();
    private readonly List<KeyValuePair<string, string>> _processes = [];

    internal string Add(DetectedGameProcess process)
    {
        var owner = OwnerOf(process);
        lock (_gate)
        {
            _processes.RemoveAll(pair => string.Equals(pair.Key, owner, StringComparison.Ordinal));
            _processes.Add(new(owner, process.GameId));
        }

        return owner;
    }

    internal string? RemoveAndFindReplacement(DetectedGameProcess process)
    {
        var owner = OwnerOf(process);
        lock (_gate)
        {
            _processes.RemoveAll(pair => string.Equals(pair.Key, owner, StringComparison.Ordinal));
            return LatestOwnerLocked(process.GameId);
        }
    }

    internal string? LatestGameId()
    {
        lock (_gate)
            return _processes.Count == 0 ? null : _processes[^1].Value;
    }

    internal string? LatestOwner(string gameId)
    {
        lock (_gate)
            return LatestOwnerLocked(gameId);
    }

    internal static string OwnerOf(DetectedGameProcess process)
    {
        var path = ProcessNameGameDetector.NormalizePath(process.ExecutablePath) ?? process.ExecutablePath;
        if (OperatingSystem.IsWindows())
            path = path.ToUpperInvariant();
        return $"{process.ProcessId}:{process.ProcessStartTime?.UtcTicks ?? 0}:{path}";
    }

    private string? LatestOwnerLocked(string gameId)
    {
        for (var index = _processes.Count - 1; index >= 0; index--)
        {
            if (string.Equals(_processes[index].Value, gameId, StringComparison.OrdinalIgnoreCase))
                return _processes[index].Key;
        }

        return null;
    }
}
