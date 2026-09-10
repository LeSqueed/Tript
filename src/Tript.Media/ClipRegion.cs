// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Media;

public readonly record struct ClipRegion(TimeSpan Start, TimeSpan End)
{
    public static ClipRegion FromSeconds(double start, double end) =>
        new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end));

    public TimeSpan Duration => End - Start;
}
