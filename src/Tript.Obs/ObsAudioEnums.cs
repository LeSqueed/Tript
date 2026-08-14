// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Obs;

// enum speaker_layout. The last member is explicitly 8 in the header and there is no member with
// value 7, so the values are written out — letting the compiler number these would silently shift
// 7.1 down by one.
public enum ObsSpeakerLayout
{
    Unknown = 0,
    Mono = 1,
    Stereo = 2,
    TwoPointOne = 3,
    FourPointZero = 4,
    FourPointOne = 5,
    FivePointOne = 6,
    SevenPointOne = 8
}
