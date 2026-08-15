// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Media;

// The two clipping modes, both post-hoc on a recorded session. "Combine" joins every region into
// one video by simple concatenation — end-to-end cut, no crossfade or overlap at the joins. The
// audio tracks line up because the regions came from the same session. "Separate" writes each
// region as its own clip file.
public enum ClipMode
{
    Combine,
    Separate
}
