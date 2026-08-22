# Tript

Tript is a clean-room screen recorder that watches for a supported game to be detected on screen and starts recording automatically, then creates bookmarks (and clips) as highlights happen. Detection runs locally from bundled ONNX models; recording is driven by OBS.

## Run modes

There are two ways to run Tript — same app host, different front end:

- **Headless** (`Tript.App`): no window. The host serves the UI over HTTP on port 2882 and prints the
  URL to open — including the per-launch key, without which every request is refused (see below). No
  browser is opened.
- **Desktop shell** (`Tript.Shell`): a native window via Photino hosting the same React UI, pointed at
  the host's own URL in-process, key included.

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

Model-training functionality is excluded from normal builds. Enable it explicitly at build time:

```sh
make TRAINING=true release
make TRAINING=true test
```

The flag is compile-time only. C# code uses the `TRIPT_TRAINING` symbol and the frontend uses
`trainingEnabled` from `src/Tript.Web/src/buildFeatures.ts`; future training code must remain behind
those guards.
Changing an environment variable after the application has been built cannot enable training.

`make dev` / `make run` / `make release` use `dist/<config>` under the repo root.

## Run

```sh
make run                        # prefers Tript.Shell, falls back to Tript.App
make run FAKE_RECORDER=false    # real recording (needs obs-studio + a display server)
```

`FAKE_RECORDER` defaults to `true`, so a dev session runs without OBS or capture hardware.

### The launch key

The host mints a 256-bit key at startup and refuses every request that does not carry it — the UI
host, the control socket and the content server alike. It lives only in memory and dies with the
process.

The desktop shell passes it to its own window in-process, so there is nothing to do. The **headless**
host prints the URL to open on its `READY` line; open that, not a bare `http://localhost:2882/`,
which is answered with 403. `vite dev` runs on **2883** so it no longer collides with the host, but it cannot mint a
key, so it is only good for rendering the UI in isolation: it serves the SPA and the app reports a
missing key rather than retrying a socket that would never be accepted.

What the key is for: the Origin check already refuses a malicious web page, but loopback is **not**
user-scoped — another user's process on the same machine reaches 127.0.0.1 and sends no Origin at
all. The key closes that, along with browser extensions (which need not present a page Origin) and
the content server, which by design cannot check Origin because a `<video>` element sends none.

What it is **not**: protection against a process running as you. That process can read the recordings
straight off disk. The key rides in the URL query, because a WebSocket handshake and a `<video src>`
have no other channel, so treat a pasted URL or a screenshot of the address bar as handing over
control for the life of that launch.

## Game catalogue and auto-record

Stable game identities and known executables are kept in the project-owned `data/games.json` catalogue.
It currently contains Overwatch. Settings retain per-game recording and capture overrides, but they do
not define the detection catalogue. If an unknown Windows process owns a fullscreen foreground window,
Tript can fall back to that executable name after ignoring system locations such as `System32` and
`WindowsApps`.

The settings file lives in the platform config directory (`$XDG_CONFIG_HOME/Tript` or `~/.config/Tript` on
Linux, `%AppData%\Tript` on Windows) or wherever you point `--settings-path`.

## Project layout

- `src/Tript.App` — headless host: UI server, recorder wiring, CLI entry point.
- `src/Tript.Shell` — desktop window: Photino webview over the app host's UI.
- `src/Tript.Web` — React front end.
- `src/Tript.Obs` — OBS binding (libobs + obs-ffmpeg-mux).
- `src/Tript.Detection` — detection models and the model service.
