# Tript

<img src="src/Tript.Web/public/tript.png" alt="Tript icon" width="96" height="96">

**Tript watches your screen while you play, and records moments you might want to share.**

![Tript's player: an Overwatch session with every kill and death marked on the timeline, and the highlights Tript cut from it lined up on the right](.github/assets/hero.jpg)

New here? The [documentation](https://github.com/LeSqueed/Tript/wiki) covers getting started, reviewing highlights, and every keyboard shortcut.

## What it does

Tript sits quietly in the background and watches for a supported game. The moment it recognizes
one, it starts recording automatically if you so desire. While you play, it keeps an eye out for the moments that matter and drops a bookmark for supported games (or a fullclip) the instant one happens, so your highlights are already marked by the time you're done playing.

All of that detection runs **entirely on your own machine**, from small bundled ML models, nothing
about your screen or your gameplay is ever uploaded anywhere without your permission.

Recording itself is handled by [OBS](https://obsproject.com/)'s engine under the hood, so capture quality and performance are the same you'd get from OBS directly.

## Support development

[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/lesqueed)

Tript started as something I built for my own use, out of frustration with other tools and the games they didn't support. Making it available like this means running a server to host detection models (so updates work, and games nobody plays don't take up space for everyone else) and to resolve automatically detected games.

With enough support, there's room to get a dedicated server for this and grow it from there. Some ideas I'd like to explore: easier ways to share gameplay, (temporarily) hosted on our own platform, and even community-trained models: opening up training to the community for new games, with people publishing their own models.

These are just ideas right now, nothing is set in stone. For things like permanent clip hosting I might eventually need membership tiers, but that's a decision for later. What I can promise: key features and quality will never be locked behind a paywall. If membership ever happens, it'll be for things where payment reflects real running costs, with maybe small extras on top. It won't be for things like high-quality recordings, automatic highlight creation, or access to finished models, which should stay free for everyone.


## Features

- **Automatic detection & recording:** Tript starts and stops recording on its own as you launch
  and quit a supported game.
- **Local-only detection:** everything runs on-device; nothing about your screen or gameplay
  leaves your machine.
- **Automatic bookmarks and clips:** highlight moments are marked (or clipped) as they happen,
  with no manual input required.
- **Manual hotkeys:** global shortcuts to toggle recording, drop a bookmark, or grab a quick clip
  whenever you want one yourself.
- **Native notifications:** a toast and a sound cue when recording starts, stops, or something
  goes wrong.
- **Streaming with OBS (Windows):** OBS and Tript record games the same way, and if both do it at once one of
  them may only see a black screen. Turn on sharing in the Streamer tab and Tript sends the game
  picture to OBS instead, with a delay of about 17 ms at 60 FPS. In OBS, install the
  [Spout2 Plugin for OBS](https://github.com/Off-World-Live/obs-spout2-plugin), add a *Spout2
  Capture* source, pick `Tript`, and use it instead of Game Capture for games Tript records. Tript
  still works without OBS, and sharing starts on its own when OBS opens.
- **Don't see your game?** You can request support for a game right from Tript's settings. Note that this sends some information about your install and machine, such as an install ID and your IP. This information is used to prevent people spamming the server and is not intended to identify an individual user. 

## Supported games

Right now there is one supported game: **Overwatch** (events should create bookmarks regardless of language, but for the best experience I'd suggest English. Other languages are planned to be added with hopefully community help and feedback). The goal is to make it relatively easy to add more games in the future, perhaps even allowing community models to be created.

## Status

Tript runs on **Windows** and **Linux**. The Windows download is self-contained and bundles
everything it needs, including OBS itself, so there's nothing separate to install. The Linux version
uses the OBS Studio from your distribution; see [Linux](#linux) below.

Windows has seen the most real use. Linux is newer and less battle tested, and so far both have
mainly been used with AMD graphics. Expect the odd rough edge there, and please report it.

## Things that need testing (uncomfirmed or likely to cause issues)

Found one of these, or something else? Join the Discord and let me know.

[![Discord](https://img.shields.io/badge/Discord-Join%20the%20server-5865F2?logo=discord&logoColor=white)](https://discord.gg/G4DR3Meumn)

- **Wide screen event detection** currently the model is trained and regions are based on a 16:9 aspect ratio. Other aspect ratio's are untested. I'd love to receive feedback with a ideally a high quality recording.
- **Non English game clients** to give the best experience, Tript tries to avoid creating events when you are dead and spectating the person who eliminated you or when you are spectating a teammate. To ensure we only show your best moments. This is done based on text on your screen. Leading to it creating bookmarks or highlights of moments that are not of interest to you.
- **Nvidia and Intel GPUs** I only had access to an AMD GPU during testing, on Windows and on Linux. Nvidia or Intel will hopefully work out of the box. Report issues if you do encounter them.
- **Sharing with OBS** works here with one GPU and one OBS install. Multi-GPU machines, other Spout
  receivers and HDR games are untested, so report anything that looks off.
- **Linux** is less battle tested than Windows. It has mainly been used on sway with an AMD GPU.
  KDE, GNOME and X11 desktops, Nvidia and Intel GPUs, and games other than Overwatch under Proton all
  need more real-world testing. Include your distribution, desktop and the log from
  `~/.local/state/Tript/logs` when you report something.
- **Automated game detection outside of Steam** - Tript tries to automatically detect when you are playing a game and add it to the games library. The only tested launcher is currently Steam. On Linux, Tript also reads Heroic and Lutris libraries, which are untested so far. If you find it is not properly detecting games, please report on this. Including your install location and what launcher you use to play the game.

## Getting started

1. Download the latest release from the
   [Releases page](https://github.com/LeSqueed/Tript/releases/latest).
2. Unzip it anywhere.
3. Run `Tript.exe`.

**Requirements:** Windows 10 or later.

### Linux

1. Install the system packages Tript uses:
   - **Debian/Ubuntu:** `obs-studio libwebkit2gtk-4.1-0 gstreamer1.0-plugins-good ffmpeg`. Ubuntu's own
     `obs-studio` can be older than 30.1; use the [OBS PPA](https://obsproject.com/kb/linux-installation)
     if it is.
   - **Fedora:** `obs-studio webkit2gtk4.1 gstreamer1-plugins-good ffmpeg` (OBS and ffmpeg from RPM Fusion).
   - **Arch:** `obs-studio webkit2gtk-4.1 gst-plugins-good ffmpeg`.
2. Download `Tript-<version>-linux-x64.tar.gz` from the
   [Releases page](https://github.com/LeSqueed/Tript/releases/latest) and unpack it.
3. Run `./install.sh` inside the unpacked folder. It installs Tript for your user, adds it to your
   application menu and lists anything that is still missing. `./install.sh --uninstall` removes it
   again and keeps your recordings and settings.

On Wayland, the first recording asks which screen to share. Tript remembers the answer. To capture a
game directly rather than the whole screen, install
[obs-vkcapture](https://github.com/nowrep/obs-vkcapture) and add `obs-gamecapture %command%` to the
game's Steam launch options.

Tript notices games from Steam, Heroic and Lutris, and any game running fullscreen in an X11 or
XWayland window. Games that run as native Wayland windows outside those launchers need adding by
hand as a custom game.

Global hotkeys use your desktop's shortcut portal on Wayland (KDE Plasma, GNOME 48 and newer), which
asks you to confirm them the first time and decides the keys. On sway and other wlroots desktops
they only work while an X11 or XWayland window, such as a Proton game, has focus. On X11 they work
directly. Tray icons, starting with the system and sharing the game picture with OBS are
Windows-only for now.

If your desktop runs in HDR mode, recordings are labelled HDR once they stop. They are 8-bit, so
smooth gradients may show some banding; 10-bit HDR recording isn't possible on Linux yet.

Linux updates are not installed automatically: Tript tells you when a new version is out and links
to its release page.

## License

Tript is licensed under the GNU General Public License v2.0 or later (GPL-2.0-or-later).

The Windows download also includes, as separate programs under their own licenses: the OBS Studio
runtime (GPL-2.0-or-later) and an FFmpeg build from [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)
(GPL-3.0, license in `App/vendor/ffmpeg/LICENSE.txt`). The exact FFmpeg release is pinned in the
`Makefile` (`FFMPEG_TAG`, `FFMPEG_BUILD`); its source is the FFmpeg commit named in the build.

---

## Building from source

Tript is also fully buildable from source, on both Windows and Linux.

**Toolchain:** .NET 10 SDK, Node.js 20.19+ (22 LTS recommended), and Git LFS (the detection models
are stored through LFS, run `git lfs pull` after cloning, or the models will just be small pointer
files and detection won't work).

**Linux system packages:** `libwebkit2gtk-4.1-0` and `gstreamer1.0-plugins-good` (both needed for
the desktop shell window), and `obs-studio` 30.1+ with `obs-ffmpeg-mux` for real recording (OBS is
discovered at runtime on Linux; Windows bundles its own copy instead). Real recording needs a
Wayland session with the PipeWire screen-capture portal, or X11. `TRIPT_DISPLAY_PLATFORM=x11|wayland`
forces one.

```sh
make web      # build the React frontend only
make shell    # frontend + publish the desktop shell into dist/<config>
make windows  # Windows self-contained publish with the bundled OBS runtime (cross-compiles from Linux)
make linux-package  # self-contained Linux tarball + .sha256 in dist/<config>-linux
make test     # .NET test suite + frontend Vitest suite
make run      # run the assembled build (prefers the desktop shell, falls back to the headless host)
```

This covers the essentials for building and running Tript yourself. The deeper internals
(resolver configuration, the per-launch security key, model training, and the full project layout)
aren't documented here; the source itself (particularly the `Makefile` and the code under
`src/Tript.App`) is thoroughly commented and is the authoritative reference for that detail.
