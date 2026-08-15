// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Media;

// A marked region of a recorded session, as the user draws it on the timeline. A region is a start
// and an end, anywhere and any length — it needs no bookmark at either bound (bookmarks are
// navigation aids, not clip instructions), and it is cut at exactly these times, never snapped to a
// keyframe.
//
// Both endpoints are offsets from the start of the session's file. The bounds are validated against
// the file's real duration before any extraction runs, so an out-of-bounds region is a clear error
// rather than a silent empty clip.
public readonly record struct ClipRegion(TimeSpan Start, TimeSpan End)
{
    public static ClipRegion FromSeconds(double start, double end) =>
        new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end));

    public TimeSpan Duration => End - Start;
}
