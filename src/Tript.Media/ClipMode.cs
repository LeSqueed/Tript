// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Media;

// The two clipping modes, both post-hoc on a recorded session. "Combine" joins every region into
// one video by simple concatenation — end-to-end cut, no crossfade or overlap at the joins.
public enum ClipMode
{
    Combine,
    Separate
}
