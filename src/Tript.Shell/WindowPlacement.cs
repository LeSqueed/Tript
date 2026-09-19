// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Shell;

internal sealed record WindowPlacement(int Left, int Top, int Width, int Height, bool Maximized)
{
    internal const int MinimumWidth = 480;
    internal const int MinimumHeight = 360;
    internal const int MaximumExtent = 32767;

    internal static WindowPlacement? Sanitize(WindowPlacement? saved)
    {
        if (saved is null)
            return null;

        if (saved.Width < MinimumWidth || saved.Height < MinimumHeight
            || saved.Width > MaximumExtent || saved.Height > MaximumExtent
            || Math.Abs(saved.Left) > MaximumExtent || Math.Abs(saved.Top) > MaximumExtent)
        {
            return null;
        }

        return saved;
    }
}
