// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Obs;

[Flags]
public enum ObsAlignment : uint
{
    Center = 0,
    Left = 1 << 0,
    Right = 1 << 1,
    Top = 1 << 2,
    Bottom = 1 << 3
}

public enum ObsBoundsType
{
    None = 0,
    Stretch,
    ScaleInner,
    ScaleOuter,
    ScaleToWidth,
    ScaleToHeight,
    MaxOnly
}

public enum ObsOrderMovement
{
    Up = 0,
    Down,
    Top,
    Bottom
}

public enum ObsBlendingMethod
{
    Default = 0,
    SrgbOff
}

public enum ObsBlendingType
{
    Normal = 0,
    Additive,
    Subtract,
    Screen,
    Multiply,
    Lighten,
    Darken
}
