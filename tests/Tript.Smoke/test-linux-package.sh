#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-2.0-or-later
# Copyright (c) 2026 LeSqueed and the Tript contributors
#
# Installs a Linux release tarball into a throwaway home and starts the installed app host until it
# reports READY, so the file users download is the one that gets started.
set -euo pipefail

[[ $# -eq 1 ]] || { echo "Usage: $0 <Tript-<version>-linux-x64.tar.gz>" >&2; exit 2; }

tarball="$(realpath "$1")"
(cd "$(dirname "$tarball")" && sha256sum -c "$(basename "$tarball").sha256")

work="$(mktemp -d "${TMPDIR:-/tmp}/tript-package-XXXXXX")"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/unpacked" "$work/home"
tar -xzf "$tarball" -C "$work/unpacked"

package="$(basename "$tarball" .tar.gz)"
HOME="$work/home" sh "$work/unpacked/$package/install.sh"

app="$work/home/.local/opt/tript/app"
"$(dirname "$0")/test-app-host-ready.sh" "$app/Tript.App" "$app/dist" 90
