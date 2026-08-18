// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using Serilog;

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

    // System.Threading.Timer does not suppress re-entry, and a subscriber can block for seconds.
    // A tick that lands while the previous one is still running is dropped rather than queued.
    private int _ticking;

    public ProcessNameGameDetector(IEnumerable<string> gameNames, TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(gameNames);

        _gameNames = gameNames.Select(NormalizeProcessName).Where(name => name.Length > 0).ToArray();
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
        if (Interlocked.CompareExchange(ref _ticking, 1, 0) != 0)
            return;

        try
        {
            Poll();
        }
        catch (Exception exception)
        {
            // Same reason as Raise: nothing may escape a timer callback.
            Log.Warning(exception, "ProcessNameGameDetector: a poll failed; the watch continues.");
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    private void Poll()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                // Each Process wraps an OS handle on Windows; a poll every few seconds that keeps
                // them all is a handle leak until the finalizers run.
                using (process)
                    names.Add(NormalizeProcessName(process.ProcessName));
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

        // The transitions are decided under the lock and raised outside it. A subscriber can block
        // for seconds (stopping a recording does), and holding _gate across that blocks Dispose and
        // every other tick behind it.
        List<string> started;
        int stopped;
        lock (_gate)
        {
            if (_disposed)
                return;

            started = seen.Except(_running).ToList();
            var gone = _running.Except(seen).ToArray();
            stopped = gone.Length;

            foreach (var game in started)
                _running.Add(game);

            foreach (var game in gone)
                _running.Remove(game);
        }

        foreach (var game in started)
            Raise(() => GameStarted?.Invoke(game));

        for (var i = 0; i < stopped; i++)
            Raise(() => GameStopped?.Invoke());
    }

    // A subscriber that throws must not take the process with it. These run on a timer callback,
    // where an escaping exception is unhandled and terminates the process — and the app's own
    // subscribers are StartRecording and StopRecording, which reach libobs and the file system.
    private static void Raise(Action raise)
    {
        try
        {
            raise();
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "ProcessNameGameDetector: a subscriber threw; the watch continues.");
        }
    }

    // The executable-name vocabulary the catalogue carries uses extensions; the process list does
    // not on Linux. Normalizing here means a catalogue entry and a running process agree on the
    // comparison regardless of platform. Public because the names this detector reports back are
    // normalized too, so anything matching one against a catalogue entry has to spell it the same
    // way rather than guess whether a `.exe` is on either side.
    public static string NormalizeProcessName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
    }
}
