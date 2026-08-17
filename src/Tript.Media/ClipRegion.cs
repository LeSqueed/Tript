// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Media;

// A marked region of a recorded session, as the user draws it on the timeline. A region is a start
// and an end, anywhere and any length — it needs no bookmark at either bound (bookmarks are
// navigation aids, not clip instructions), and it is cut at exactly these times, never snapped to a
// keyframe.
//
// Both endpoints are offsets from the start of the session's file. The bounds are fitted to the
// file's real duration before any extraction runs (ClipRegionBounds), so an out-of-bounds region is
// either clamped to something cuttable or dropped — never handed to ffmpeg, which reports every
// out-of-bounds region as success and an empty file.
public readonly record struct ClipRegion(TimeSpan Start, TimeSpan End)
{
    // The direct conversion, for regions built from values already known to be real times (the test
    // suite, and any in-process caller). It inherits TimeSpan.FromSeconds' behaviour on non-finite
    // and over-range input, which is to throw: use ClipRegionBounds.TryFromSeconds for anything that
    // came off the wire.
    public static ClipRegion FromSeconds(double start, double end) =>
        new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end));

    public TimeSpan Duration => End - Start;
}
