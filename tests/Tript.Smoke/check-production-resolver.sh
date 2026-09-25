#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-2.0-or-later
# Copyright (c) 2026 LeSqueed and the Tript contributors
#
# Reads resolver.json from stdin and fails unless it points at an https resolver with a key, so a
# release never ships the checked-in localhost default.
set -euo pipefail

jq -e '(.url | startswith("https://")) and (.apiKey | type == "string" and length > 0)' >/dev/null || {
  echo "::error title=resolver.json::the release does not point at the production resolver"
  exit 1
}
