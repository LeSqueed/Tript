// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Obs;

// The OBS_ALIGN_* bits, used both for a scene item's own alignment and for the alignment of its
// image inside its bounds. Center is the absence of every bit rather than a bit of its own, so
// combining it with anything else is meaningless rather than wrong.
[Flags]
public enum ObsAlignment : uint
{
    Center = 0,
    Left = 1 << 0,
    Right = 1 << 1,
    Top = 1 << 2,
    Bottom = 1 << 3
}

// enum obs_bounds_type — how a scene item's image is fitted into its bounding box. None means the
// bounds are ignored entirely and the item is drawn at its own size.
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

// enum obs_order_movement. A relative move, which is why there is a separate call for setting an
// absolute order position.
public enum ObsOrderMovement
{
    Up = 0,
    Down,
    Top,
    Bottom
}

// enum obs_blending_method. SrgbOff turns off the sRGB conversion for the item, which matters only
// when something upstream has already done it.
public enum ObsBlendingMethod
{
    Default = 0,
    SrgbOff
}

// enum obs_blending_type.
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
