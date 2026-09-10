// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Obs;

public enum ObsEncoderType
{
    Audio = 0,
    Video = 1
}

[Flags]
public enum ObsEncoderCaps : uint
{
    None = 0,
    Deprecated = 1 << 0,
    PassTexture = 1 << 1,
    DynBitrate = 1 << 2,
    Internal = 1 << 3,
    Roi = 1 << 4,
    Scaling = 1 << 5,
    MultitrackDynBitrate = 1 << 6
}

public enum ObsPropertyType
{
    Invalid = 0,
    Bool,
    Int,
    Float,
    Text,
    Path,
    List,
    Color,
    Button,
    Font,
    EditableList,
    FrameRate,
    Group,
    ColorAlpha
}

public enum ObsComboFormat
{
    Invalid = 0,
    Int,
    Float,
    String,
    Bool
}

public readonly record struct ObsEncoderRoi(uint Top, uint Bottom, uint Left, uint Right, float Priority)
{
    public static ObsEncoderRoi Square(uint x, uint y, uint size, float priority) =>
        new(y, y + size, x, x + size, priority);
}
