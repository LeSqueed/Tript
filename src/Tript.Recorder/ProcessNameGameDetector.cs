// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;

namespace Tript.Recorder;

// The minimal process-watcher the alpha ships: polls the running process list on a timer and raises
// the detector events when a process whose name matches the known-game list appears or disappears.
// This is deliberately not the full detection ladder from the games-catalogue spec — no executable
// path patterns, no Steam/Proton resolution, no blacklist — it is the seam made real so the
// recorder can be exercised end to end.
public sealed class ProcessNameGameDetector : IGameDetector
{
    private readonly TimeSpan _pollInterval;
    private readonly string[] _gameNames;

    private readonly object _gate = new();
    private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _timer;
    private bool _disposed;

    public ProcessNameGameDetector(IEnumerable<string> gameNames, TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(gameNames);

        _gameNames = gameNames.Select(Normalize).Where(name => name.Length > 0).ToArray();
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
    }

    public event Action<string>? GameStarted;

    public event Action? GameStopped;

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            if (_timer is not null)
                return;

            // The first poll runs immediately so a game already running when the app starts is
            // detected without waiting a full interval.
            _timer = new Timer(OnTick, null, TimeSpan.Zero, _pollInterval);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void OnTick(object? state)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                names.Add(Normalize(process.ProcessName));
            }
        }
        catch
        {
            // A process list snapshot is best-effort; a failure to read it is not a reason to stop
            // watching. The next tick retries.
            return;
        }

        foreach (var game in _gameNames)
        {
            if (names.Contains(game))
                seen.Add(game);
        }

        lock (_gate)
        {
            if (_disposed)
                return;

            foreach (var game in seen.Except(_running))
            {
                _running.Add(game);
                GameStarted?.Invoke(game);
            }

            foreach (var gone in _running.Except(seen).ToArray())
            {
                _running.Remove(gone);
                GameStopped?.Invoke();
            }
        }
    }

    // The executable-name vocabulary the catalogue carries uses extensions; the process list does
    // not on Linux. Normalizing here means a catalogue entry and a running process agree on the
    // comparison regardless of platform.
    private static string Normalize(string name)
    {
        var trimmed = name.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
    }
}
