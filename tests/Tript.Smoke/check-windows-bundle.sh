#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-2.0-or-later
# Copyright (c) 2026 LeSqueed and the Tript contributors
#
# Checks the Windows release zip holds everything Tript needs to start and record. The bundle ships
# only the desktop shell, which needs a desktop to run, so its contents are checked instead.
set -euo pipefail

[[ $# -eq 1 ]] || { echo "Usage: $0 <Tript-<version>-win-x64.zip>" >&2; exit 2; }

zip="$(realpath "$1")"
(cd "$(dirname "$zip")" && sha256sum -c "$(basename "$zip").sha256")

listing="$(unzip -Z1 "$zip")"
missing=0
for file in \
    Tript/Tript.exe \
    Tript/App/Tript.Shell.exe \
    Tript/App/resolver.json \
    Tript/App/dist/index.html \
    Tript/App/bin/64bit/obs.dll \
    Tript/App/obs-plugins/64bit/win-capture.dll \
    Tript/App/obs-ffmpeg-mux.exe \
    Tript/App/vendor/ffmpeg/ffmpeg.exe \
    Tript/App/vendor/ffmpeg/ffprobe.exe \
    Tript/App/vendor/ffmpeg/LICENSE.txt; do
  if ! grep -qxF "$file" <<<"$listing"; then
    echo "::error title=Windows bundle::$file is missing from $(basename "$zip")"
    missing=1
  fi
done
exit $missing
