// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Obs;

// Explicit values: libobs has no 7, so compiler numbering would shift SevenPointOne.
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
