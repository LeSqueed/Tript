#!/bin/sh
# SPDX-License-Identifier: GPL-2.0-or-later
# Copyright (c) 2026 LeSqueed and the Tript contributors
set -eu

package_dir="$(dirname "$(readlink -f "$0")")"
data_home="${XDG_DATA_HOME:-$HOME/.local/share}"
install_dir="${TRIPT_INSTALL_DIR:-$HOME/.local/opt/tript}"
bin_dir="$HOME/.local/bin"
desktop_file="$data_home/applications/io.github.lesqueed.Tript.desktop"
icon_file="$data_home/icons/hicolor/256x256/apps/tript.png"

usage() {
    echo "Usage: $0 [--uninstall]"
    echo "Installs Tript for the current user into $install_dir."
}

refresh_desktop_caches() {
    command -v update-desktop-database >/dev/null 2>&1 &&
        update-desktop-database -q "$data_home/applications" 2>/dev/null || true
    command -v gtk-update-icon-cache >/dev/null 2>&1 &&
        gtk-update-icon-cache -q -t "$data_home/icons/hicolor" 2>/dev/null || true
}

uninstall() {
    rm -rf "$install_dir"
    rm -f "$bin_dir/tript" "$desktop_file" "$icon_file"
    refresh_desktop_caches
    echo "Tript was removed. Recordings, settings and logs were left in place."
}

find_muxer() {
    if command -v obs-ffmpeg-mux >/dev/null 2>&1; then
        command -v obs-ffmpeg-mux
        return
    fi
    for dir in /usr/lib/x86_64-linux-gnu/obs-plugins /usr/lib64/obs-plugins /usr/lib/obs-plugins \
        /usr/local/lib/obs-plugins /usr/local/bin /usr/bin; do
        if [ -x "$dir/obs-ffmpeg-mux" ]; then
            echo "$dir/obs-ffmpeg-mux"
            return
        fi
    done
}

has_library() {
    ldconfig -p 2>/dev/null | grep -q "$1" ||
        ls /usr/lib/x86_64-linux-gnu/"$1"* /usr/lib64/"$1"* /usr/lib/"$1"* /usr/local/lib/"$1"* >/dev/null 2>&1
}

report_missing_dependencies() {
    missing=""
    has_library libobs.so || missing="$missing\n  - OBS Studio 30.1 or newer (package: obs-studio)"
    has_library libwebkit2gtk-4.1.so || missing="$missing\n  - WebKitGTK 4.1 (Debian/Ubuntu: libwebkit2gtk-4.1-0, Fedora: webkit2gtk4.1, Arch: webkit2gtk-4.1)"
    command -v gst-inspect-1.0 >/dev/null 2>&1 && ! gst-inspect-1.0 autoaudiosink >/dev/null 2>&1 &&
        missing="$missing\n  - GStreamer good plugins (gstreamer1.0-plugins-good / gst-plugins-good)"
    [ -n "$(find_muxer)" ] || missing="$missing\n  - obs-ffmpeg-mux (ships with obs-studio)"
    command -v ffmpeg >/dev/null 2>&1 || missing="$missing\n  - ffmpeg (for clips and thumbnails)"

    if [ -n "$missing" ]; then
        printf "\nTript is installed, but these are missing:%b\n" "$missing"
    fi
}

install() {
    staging="$install_dir.new"
    rm -rf "$staging"
    mkdir -p "$staging"
    cp -a "$package_dir/app" "$package_dir/tript" "$staging/"
    rm -rf "$install_dir"
    mv "$staging" "$install_dir"

    muxer="$(find_muxer)"
    [ -n "$muxer" ] && ln -sf "$muxer" "$install_dir/app/obs-ffmpeg-mux"

    mkdir -p "$bin_dir" "$(dirname "$desktop_file")" "$(dirname "$icon_file")"
    ln -sf "$install_dir/tript" "$bin_dir/tript"
    sed "s#@EXEC@#$install_dir/tript#" "$package_dir/tript.desktop.in" >"$desktop_file"
    cp "$install_dir/app/dist/tript.png" "$icon_file"
    refresh_desktop_caches

    echo "Tript is installed. Start it from your application menu or run: tript"
    case ":$PATH:" in
        *":$bin_dir:"*) ;;
        *) echo "Note: $bin_dir is not on your PATH." ;;
    esac
    report_missing_dependencies
}

case "${1:-}" in
    "") install ;;
    --uninstall) uninstall ;;
    -h | --help) usage ;;
    *) usage >&2; exit 2 ;;
esac
