# Changelog

All notable changes to Tript are recorded here. Versions before 0.1.0-alpha.10 were not tracked in
this file; their notes are on the [GitHub releases page](https://github.com/LeSqueed/Tript/releases).

## Unreleased

### Recording you can rely on

- **Recordings survive a crash or power loss.** Sessions are written with OBS's Hybrid MP4 writer,
  so a hard exit leaves a playable file instead of an unreadable one. When the running OBS is older
  than 31 Tript falls back to the previous writer and says so in the log.
- **Bookmarks are saved while you record**, every 15 seconds, instead of only when the recording
  stops. A crash now costs at most the last few seconds of bookmarks, not the whole session.
- **Windows restart, shutdown and sign-out stop the recording cleanly** first, so the file and its
  bookmarks are finished before Windows closes Tript.
- **Quitting while a recording is slow to stop no longer loses its bookmarks.** Shutdown now waits
  for the stop to finish before it writes the session's details.

### Updates

- **A broken update rolls itself back.** If a new version fails to start within a minute of being
  installed, Tript puts the previous version back, tells you, and does not install that release
  again automatically.
- **An update interrupted by a power cut no longer breaks the install.** Tript recovers the previous
  version instead of asking you to reinstall.
- **Turning off "Check for updates automatically" now actually stops automatic updates.** Before,
  the setting was only read at startup and the daily check ignored it.

### Clips are made on your graphics card

- **Creating clips and converting HDR to SDR now run on the GPU.** Tript encodes with NVIDIA NVENC,
  AMD AMF or Intel Quick Sync, whichever works on your machine, decodes on the GPU, and tone maps HDR
  recordings on the GPU through Vulkan. A 20 second 1440p HDR clip went from about 6 minutes of CPU
  time to under 20 seconds, and finishes three times sooner. The CPU is only used when no GPU path
  works, and a clip that fails on the GPU is retried on the CPU instead of failing.
- **ffmpeg now ships with Tript on Windows**, so clips, thumbnails and SDR conversion work without
  installing anything else.
- **The clip dialog remembers "One merged clip" or "Separate clips"** instead of resetting every time.
- **The player's playlist updates as clips and highlights are added**, instead of only after you
  reopen the player.

### The tray icon shows what Tript is doing

- A red dot while recording a session, a purple ring while only the replay buffer runs, and an amber
  or red triangle when something needs attention (low disk space, recording on hold, sharing to OBS
  failed). Hover for the details, or right-click to see the problem at the top of the menu.
- The in-app recording indicator uses the same purple ring for buffer-only recording, so the two
  states are easy to tell apart.

### When something goes wrong

- **Tript tells you when it cannot start**, with the reason and where the log is, instead of silently
  doing nothing. The most common case is a missing or damaged OBS runtime.
- **Settings, General, Diagnostics has an "Open log folder" button**, so you can find your logs when
  reporting a problem.
- If the window itself runs into a problem it shows a **Reload window** screen instead of going
  blank. Recording keeps running underneath.
- A damaged `settings.json` no longer stops Tript from starting. It starts with default settings and
  keeps the unreadable file next to it, named `settings.json.unreadable-<date>`, so nothing is lost.

### Reliability

- ffmpeg and training processes are now tied to Tript and stop when it does, however it exits.
  Previously a crash could leave them running, holding a clip file open or GPU memory for hours.
- A clip whose ffmpeg run stops making progress is cancelled after five minutes instead of blocking
  the clip queue forever.
- Several shutdown paths that could hang indefinitely now give up after a bounded wait.
- A background failure in a hotkey, tray action or automatic recording start is logged instead of
  closing Tript.
- Fixed memory corruption in the audio device list, which was refreshed every five seconds.
- Fixed a use-after-free when listing scene items, and callbacks that could outlive OBS shutdown.
- Sharing to OBS over Spout no longer risks corrupting another application's sender registration
  when the shared list is busy.
- Live highlights no longer slow down over a long session, and finished work is released as it
  completes.
- A media file changed on disk, such as the reused replay scratch file, is re-read rather than served
  a stale duration, which could cut live highlight clips at the wrong length.

### Logging

- libobs messages (graphics device errors, encoder failures, capture hook problems) are now written
  to Tript's log. They were previously discarded.
- Log files roll over at 20 MB and are pruned during long sessions, not only at startup.
- Detection no longer writes several lines a second to the log for the whole recording.
- Log files never contain the resolver API key.
- Release builds log at the normal level and no longer write debug detail. Start Tript with
  `--verbose-log` to include it when investigating a problem.

### For developers

- CI now builds, tests and starts Tript on Windows, not only on Linux, and a release cannot publish
  until those checks pass.
- The launcher's update, rollback and recovery paths have a test suite
  (`tests/Tript.Launcher.Tests/Test-Launcher.ps1`).
- `Tript.Obs.IntegrationTests` is part of the solution again, so it is compiled on every build.
- `Tript.App` accepts `--log-dir <dir>`. The test driver uses it so test runs no longer write into,
  and prune, the real user's log folder.
