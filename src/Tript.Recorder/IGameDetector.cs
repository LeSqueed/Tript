// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Recorder;

// The auto-start seam. The recorder starts when a supported game is detected running and stops when
// it is no longer detected — it is the consumer of detection, not the detector: a real process
// watcher (WMI watchers and foreground hook on Windows, /proc polling on Linux, per the
// games-catalogue spec) publishes here, and the recorder reacts. The event is raised from whatever
// thread noticed the change; the recorder marshals it onto its own thread.
//
// The detection vocabulary is deliberately the two things that matter: a game appeared, and a game
// is gone. Which process matched, and whether it was an explicit per-game setting or the catalogue,
// is the watcher's answer to report, not the recorder's to guess.
public interface IGameDetector : IDisposable
{
    // Fires when a supported game starts running. The name is the session's game name — the
    // catalogue name when detection came from the catalogue, the per-game name when a GameSetting
    // forced it.
    event Action<string>? GameStarted;

    // Fires when a detected game is no longer running. Only a game that was reported started can be
    // reported stopped.
    event Action? GameStopped;

    // Begins watching. May be called once; Start again is a no-op.
    void Start();
}
