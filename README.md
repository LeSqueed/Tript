# Tript

Tript is a clean-room screen recorder that watches for a supported game to be detected on screen and starts recording automatically, then creates bookmarks (and clips) as highlights happen. Detection runs locally from bundled ONNX models; recording is driven by OBS.

## Run modes

There are two ways to run Tript — same app host, different front end:

- **Headless** (`Tript.App`): no window. The host starts and serves the UI over HTTP; it opens your default browser at <http://localhost:2882/> (best-effort) and prints the URL too.
- **Desktop shell** (`Tript.Shell`): a native window via Photino that hosts the same React UI, pointing the webview at `http://localhost:2882/`.

`make run` prefers the shell binary when it has been built (`make shell`) and falls back to the headless host otherwise.

## Linux system dependencies

- **`webkit2gtk-4.1`** — required for the desktop shell. This is the 4.1 ABI, **not** `webkitgtk-6.0`.
  - Arch / CachyOS: `sudo pacman -S webkit2gtk-4.1`
  - Debian / Ubuntu: `sudo apt install libwebkit2gtk-4.1-0`
- **`obs-studio` + `obs-ffmpeg-mux`** — required for real recording. Discovered at runtime; on Windows OBS is bundled instead.
  - Arch / CachyOS: `sudo pacman -S obs-studio`
  - Debian / Ubuntu: `sudo apt install obs-studio obs-ffmpeg-mux` (or your distro's equivalent)
- **A display server** (X11, or Wayland with XWayland) for real recording.

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
