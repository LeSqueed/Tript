#!/usr/bin/env bash
# Reports what ffprobe sees in one media file as a single JSON object on stdout:
#
#   {"format": {...}, "video": {...}, "audio": [{...}], "audio_track_count": N}
#
# "video" is the first video stream and "audio" every audio stream, both straight from ffprobe so
# the field types are ffprobe's own (width/height numbers, sample_rate/bit_rate strings). The four
# colour fields are always present, falling back to "unspecified" when the file does not carry them.
#
# A file ffprobe cannot read is not an error here: it reports empty objects, which is how the
# killed-muxer test proves an unfinalised recording is not a playable MP4. Only a missing argument
# or a missing ffprobe exits non-zero.
set -uo pipefail

if [ $# -lt 1 ]; then
    echo "usage: probe-media.sh <media-file>" >&2
    exit 2
fi

if ! command -v ffprobe >/dev/null 2>&1; then
    echo "ffprobe is not installed." >&2
    exit 3
fi

probe=$(ffprobe -v error -print_format json -show_format -show_streams "$1" 2>/dev/null)

PROBE_JSON="$probe" python3 - <<'PY'
import json, os, sys

try:
    probe = json.loads(os.environ["PROBE_JSON"] or "{}")
except json.JSONDecodeError:
    probe = {}

streams = probe.get("streams") or []
video = next((s for s in streams if s.get("codec_type") == "video"), {})
audio = [s for s in streams if s.get("codec_type") == "audio"]

if video:
    for field in ("color_transfer", "color_primaries", "color_space", "color_range"):
        video.setdefault(field, "unspecified")

json.dump({
    "format": probe.get("format") or {},
    "video": video,
    "audio": audio,
    "audio_track_count": len(audio),
}, sys.stdout)
PY
