// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Media;

// How one audio track of the source is treated in the clip. Track order in the source file is the
// key — recording metadata can name tracks, but the engine works against the file the session
// actually produced.
public readonly record struct AudioTrackAdjustment(
    int SourceTrackIndex,
    double Volume = 1.0,
    bool Muted = false)
{
    public static AudioTrackAdjustment Mute(int sourceTrackIndex) => new(sourceTrackIndex, 0.0, Muted: true);
}
