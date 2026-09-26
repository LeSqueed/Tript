# Changelog

All notable changes to Tript are recorded here. Versions before 0.1.0-alpha.10 were not tracked in
this file; their notes are on the [GitHub releases page](https://github.com/LeSqueed/Tript/releases).

## 1.1.1 (2026-09-26)

### Linux

- **HDR games captured through obs-vkcapture are recorded in HDR.** Tript never asked the captured
  game whether it was in HDR, so these recordings always came out SDR. They are now recorded in
  10-bit HDR. Screen capture of an HDR desktop is still 8-bit and labelled HDR.
- **Tript captures the game, not its wrapper.** With gamescope or a launch script, obs-vkcapture
  could pick up the wrapper instead of the game.
- **A slow-loading game is still captured directly.** Tript gave up on obs-vkcapture after ten
  seconds and recorded the screen for the whole session. It now waits up to two minutes for a game
  started with obs-gamecapture, and records the screen straight away for a game started without it.

## 1.1.0 (2026-09-25)

Tript now runs on Linux as well as Windows. Linux is less battle tested than Windows, so please
report anything that looks wrong.

### Linux

- **Tript now has a Linux download.** Releases carry `Tript-<version>-linux-x64.tar.gz`. Unpack it
  and run `./install.sh` to install Tript for your user, with a launcher and an application-menu
  entry. .NET is included. OBS Studio 30.1 or later, WebKitGTK 4.1 and the GStreamer good plugins
  come from your distribution, and the installer lists any that are missing.
- **Recording works on Wayland.** Tript captures the screen through the desktop's screen-sharing
  prompt, which appears the first time you record rather than at launch. Your choice is remembered,
  and "Choose a different screen" under Settings → Capture asks again. X11 desktops still use X11
  screen capture.
- **Games are captured directly when obs-vkcapture is installed**, the same way Windows captures
  them. Without it, Tript records the screen, including when "Game" capture is selected, instead
  of producing a black video.
- **Global hotkeys work on Linux.** On Wayland they go through the desktop's shortcut portal, which
  asks you to confirm them once. The Hotkeys page then shows the keys your desktop assigned and opens
  its shortcut settings to change them. On X11 they work directly. Where neither is available, the
  hotkey settings say so.
- **Notifications and sounds on Linux**: desktop notifications with the Tript icon, and the same start
  and stop sounds as on Windows.
- **Steam games are found on Linux**, including Flatpak and Snap Steam installs, and games running
  under Proton are recognised when you launch them.
- **Heroic and Lutris libraries are read too**, so Epic, GOG and Lutris games are recognised and
  named after their library entry.
- **New games are noticed while you play them.** A game running from one of those libraries, or
  any game running fullscreen in an X11 or XWayland window, is offered for your library, as the
  fullscreen detection does on Windows.
- **Recordings of an HDR desktop are labelled HDR.** They used to look grey and washed out. They
  stay 8-bit, so smooth gradients may show some banding.
- **The audio settings list your real Linux devices**, not only the default one.
- **The default recording folder follows your desktop's Videos folder**, including a translated one.
- **Logs moved to `~/.local/state/Tript/logs`.**
- **Clips are encoded on the right GPU** on machines with more than one. `TRIPT_VAAPI_DEVICE` picks
  one explicitly.
- **"Open file location" works for folders with spaces in their names.**
- **Clip exports stop when Tript exits**, instead of carrying on in the background.
- **Settings only show what works on Linux.** The tray, start-with-system and OBS sharing options are
  hidden there.
- **Tript explains why it did not start.** A missing OBS, WebKitGTK or GStreamer plugin now shows a
  dialog instead of doing nothing.
- **Closing the window, logging out or shutting down finishes the recording first**, so the file
  stays playable.
- **The Linux build only offers updates that include a Linux download.**
- Linux is less battle tested than Windows, and has mainly been used on sway with AMD graphics.
  Please report anything that looks wrong.

### Detection

- **Detection starts as soon as a game's model arrives.** A recording that began while the model
  was still downloading used to run without automatic bookmarks until the next recording.

### Games

- The example path under "Add a custom game" shows single backslashes on Windows.

### Updates

- **Tript only updates to stable releases.** Alpha and beta builds published on GitHub are no longer
  offered as updates.

## 1.0.1 (2026-09-22)

### Library

- **Ticking a card in selection mode no longer opens the delete dialog.** The card's delete button
  sat on top of its checkbox, so a click meant to select an item asked to delete it instead. In
  selection mode, cards now show only the checkbox; delete the selection with the button at the top.
- **Thumbnails of HDR recordings are no longer washed out.** They are now converted to normal
  colours the way SDR clips are. Existing thumbnails are rebuilt the next time they are shown.
- A card's length no longer covers its size and other details.

### Player

- **"Create as" now starts on the option you chose last time.** It used to show "One merged clip"
  until you opened Adjust details, so a quick Create clips could make one merged clip when you had
  asked for separate ones.
- The start and end boxes in the Create clip dialog are wide enough for times past 99 seconds.

## 1.0.0 (2026-09-22)

First stable release. It carries everything from the 0.1.0 alpha series, with one further fix.

### Updates

- **An update is no longer skipped when the previous version's backup is still on disk.** Restarting
  within two minutes of an update left that copy in place, which silently blocked the next update,
  and each restart began the wait again. The backup is now moved aside and cleaned up in the
  background.

### Reliability

- **Installing a detection model no longer fails when a virus scanner is still reading it.** The
  freshly downloaded file could be held open for a moment, which made the install give up.

### Known limits

- Tript is not code signed and has no installer, so Windows SmartScreen warns the first time you run
  it. Choose "More info", then "Run anyway".
- Clips use your graphics card where one works. NVENC and Quick Sync have been tested less widely
  than AMD; if neither works, clips are made on the CPU, which is slower but produces the same file.

## 0.1.0-alpha.11 (2026-09-22)

### Updates

- **Tript no longer closes when it starts with an update waiting.** The "update ready" notice could
  arrive before the window existed, which crashed Tript right after a restart.
- **An update is no longer skipped when the Tript folder is in use.** Tript waits a few seconds for
  the previous version to finish closing. If a window such as File Explorer still has the folder
  open, it asks you to close it and retry, instead of quietly starting the old version again.
- The log now records each step of an update: found, downloaded and verified, installing, and the
  previous version removed.

### Reliability

- **A clip, thumbnail or duration check no longer fails when Tript is busy.** Output from ffmpeg and
  ffprobe is now read on its own thread, so a file could be reported as unreadable while the machine
  was under load even though nothing was wrong with it.

### For developers

- Training looks for its Python environment in the install root (`.venv` beside `App`) first, so an
  update no longer deletes it. A `.venv` inside `App` is still used when the root has none.

## 0.1.0-alpha.10 (2026-09-22)

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
