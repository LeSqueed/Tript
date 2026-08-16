// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;

namespace Tript.Recorder;

// The auto-start seam wired to the recorder: when the detector says a game started, the recorder is
// asked to start recording; when it says the game went away, the recorder is asked to stop for a
// game end. The coordinator is where the game's name reaches the resolved settings — per the
// games-catalogue spec the effective settings are resolved when a game is detected, with the
// detected game's identity selecting any per-game override.
//
// The recorder is deliberately not told about the detector: it sees Start and Stop calls, and this
// type is what turns detector events into those calls. That is what keeps the state machine testable
// without a detector and the detector swappable without touching the recorder.
public sealed class AutoStartCoordinator : IDisposable
{
    private readonly Recorder _recorder;
    private readonly IGameDetector _detector;
    private readonly Func<string, ResolvedRecorderSettings> _resolve;

    private bool _disposed;

    public AutoStartCoordinator(Recorder recorder, IGameDetector detector, Func<string, ResolvedRecorderSettings> resolve)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(resolve);

        _recorder = recorder;
        _detector = detector;
        _resolve = resolve;
    }

    // Subscribes to the detector and starts it. Idempotent.
    public void Start()
    {
        _detector.GameStarted += OnGameStarted;
        _detector.GameStopped += OnGameStopped;
        _detector.Start();
    }

    private void OnGameStarted(string gameName)
    {
        if (_disposed)
            return;

        // The recorder refuses a start while one is in flight; a second game detected during a
        // recording simply does not disturb it.
        var settings = _resolve(gameName);
        _recorder.Start(settings);
    }

    private void OnGameStopped()
    {
        if (_disposed)
            return;

        _recorder.StopForGameEnd();
    }

    public void Dispose()
    {
        _disposed = true;
        _detector.GameStarted -= OnGameStarted;
        _detector.GameStopped -= OnGameStopped;
    }
}
