#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-2.0-or-later
# Copyright (c) 2026 LeSqueed and the Tript contributors
#
# Starts a built Tript.App headless and waits for its READY line: the Linux counterpart of
# Test-AppHostReady.ps1. Every path, port and the log folder point into a temp directory so a run
# on a developer machine never touches the real user's settings, recordings or logs.
set -euo pipefail

usage() { echo "Usage: $0 <path-to-Tript.App> <web-root> [timeout-seconds]" >&2; exit 2; }
[[ $# -ge 2 ]] || usage

app_host="$1"
web_root="$2"
timeout_seconds="${3:-60}"
[[ -x "$app_host" ]] || { echo "The app host was not found at $app_host." >&2; exit 1; }

work="$(mktemp -d "${TMPDIR:-/tmp}/tript-smoke-XXXXXX")"
mkdir -p "$work/content" "$work/logs"
: >"$work/stdout"
trap 'kill "$pid" 2>/dev/null || true; wait "$pid" 2>/dev/null || true; rm -rf "$work"' EXIT

free_port() {
    python3 -c 'import socket; s = socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1]); s.close()'
}

"$app_host" --fake-recorder --disable-updater \
    --content-root "$work/content" \
    --settings-path "$work/settings.json" \
    --log-dir "$work/logs" \
    --web-root "$web_root" \
    --ui-port "$(free_port)" --content-port "$(free_port)" --control-port "$(free_port)" \
    >"$work/stdout" 2>"$work/stderr" &
pid=$!

deadline=$((SECONDS + timeout_seconds))
while ((SECONDS < deadline)); do
    if grep -q '^READY' "$work/stdout"; then
        echo "The app host reached READY."
        exit 0
    fi
    kill -0 "$pid" 2>/dev/null || break
    sleep 1
done

echo "The app host did not reach READY within ${timeout_seconds}s."
echo "--- stderr ---"
cat "$work/stderr"
for log in "$work"/logs/tript-*.log; do
    [[ -f "$log" ]] || continue
    echo "--- log ---"
    cat "$log"
    break
done
exit 1
