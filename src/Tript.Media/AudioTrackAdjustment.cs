// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Media;

public readonly record struct AudioTrackAdjustment(
    int SourceTrackIndex,
    double Volume = 1.0,
    bool Muted = false)
{
    public static AudioTrackAdjustment Mute(int sourceTrackIndex) => new(sourceTrackIndex, 0.0, Muted: true);
}
