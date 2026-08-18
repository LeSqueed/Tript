# Tript

Tript is a clean-room screen recorder that watches for a supported game to be detected on screen and starts recording automatically, then creates bookmarks (and clips) as highlights happen. Detection runs locally from bundled ONNX models; recording is driven by OBS.

## Run modes

There are two ways to run Tript — same app host, different front end:

- **Headless** (`Tript.App`): no window. The host starts and serves the UI over HTTP at <http://localhost:2882/> and prints the URL; no browser is opened.
- **Desktop shell** (`Tript.Shell`): a native window via Photino that hosts the same React UI, pointing the webview at `http://localhost:2882/`.

`make run` prefers the shell binary when it has been built (`make shell`) and falls back to the headless host otherwise.

## Linux system dependencies

- **`webkit2gtk-4.1`** — required for the desktop shell. This is the 4.1 ABI, **not** `webkitgtk-6.0`.
  - Arch / CachyOS: `sudo pacman -S webkit2gtk-4.1`
  - Debian / Ubuntu: `sudo apt install libwebkit2gtk-4.1-0`
- **`gst-plugins-good`** — also required for the desktop shell, even though nothing in the UI
  plays audio. WebKitGTK builds a GStreamer media pipeline at startup and needs an audio sink
  (`autoaudiosink`, which lives in this package). Without it the render process aborts and the
  window appears frozen while the host keeps running, so the symptom points nowhere near the
  cause. The tell is `GStreamer element autoaudiosink not found` followed by a
  `g_signal_connect_data` NULL-instance assertion.
  - Arch / CachyOS: `sudo pacman -S gst-plugins-good`
  - Debian / Ubuntu: `sudo apt install gstreamer1.0-plugins-good`
- **`obs-studio` 30.1 or newer**, plus `obs-ffmpeg-mux` — required for real recording. Discovered at
  runtime; on Windows OBS is bundled instead (pinned to the version in the Makefile's `OBS_VERSION`).
  - Arch / CachyOS: `sudo pacman -S obs-studio`
  - Debian / Ubuntu: `sudo apt install obs-studio obs-ffmpeg-mux` (or your distro's equivalent)

  **The 30.1 floor is real, not a preference.** The binding P/Invokes entry points that do not exist
  in earlier builds — `obs_sceneitem_set_info2`, `obs_sceneitem_set_bounds_crop`,
  `obs_encoder_add_roi` and the encoder colour-space setters among them — and a missing entry point
  surfaces as an `EntryPointNotFoundException` at the moment it is first called, not at startup.
  Ubuntu 24.04 ships OBS **30.0.2**, which is below the floor; the obsproject PPA has a current build.
- **`libobs-dev`** — only needed to run the OBS integration test suite. libobs `dlopen`s its graphics
  module by unversioned name (`libobs-opengl.so`), and the runtime package ships only the versioned
  `.so.N` symlinks, so without the dev package every `obs_reset_video` reports
  `GraphicsModuleNotFound`.
- **A display server** (X11, or Wayland with XWayland) for real recording.

## Build toolchain

- **.NET 10 SDK** — every project targets `net10.0`.
- **Git LFS** — the detection models (`data/models/**/*.onnx`) are stored through LFS. Install it
  *before* cloning (`git lfs install`), or run `git lfs pull` afterwards. Without it the working tree
  gets ~130-byte pointer files where the models should be, and the detection layer reports no model
  for every game — auto-record and bookmarks then do nothing, with no other symptom.
- **Node.js 20.19+** (22 LTS recommended) with its bundled npm, for the frontend. `make web` runs
  `npm ci` and `make test` runs `npx vitest`; the Vite 8 / Vitest 4 toolchain uses `node:util`'s
  `styleText`, so an older Node fails at startup with a bare `SyntaxError` rather than a version
  message. An npm older than 7 cannot read the lockfile format either.

## Build

```sh
make web      # build the React frontend only
make shell    # frontend + publish the desktop shell into dist/<config>
make windows  # Windows self-contained publish with the bundled OBS runtime (cross-compiles from Linux)
make test     # .NET test suite + frontend Vitest suite
```

`make dev` / `make run` / `make release` use `dist/<config>` under the repo root.

## Run

```sh
make run                        # prefers Tript.Shell, falls back to Tript.App
make run FAKE_RECORDER=false    # real recording (needs obs-studio + a display server)
```

`FAKE_RECORDER` defaults to `true`, so a dev session runs without OBS or capture hardware.

## First launch: seed the game list for auto-record

The host reads `settings.Game.GameList` for the game catalogue; auto-start only fires when a game is in settings. If your settings file has no games yet, add an entry:

```json
{ "game": { "gameList": [ { "id": "Overwatch", "name": "Overwatch" } ] } }
```

The settings file lives in the platform config directory (`$XDG_CONFIG_HOME/Tript` or `~/.config/Tript` on Linux, `%AppData%\Tript` on Windows) or wherever you point `--settings-path`.

## Project layout

- `src/Tript.App` — headless host: UI server, recorder wiring, CLI entry point.
- `src/Tript.Shell` — desktop window: Photino webview over the app host's UI.
- `src/Tript.Web` — React front end.
- `src/Tript.Obs` — OBS binding (libobs + obs-ffmpeg-mux).
- `src/Tript.Detection` — detection models and the model service.
