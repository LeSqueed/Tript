// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Obs;

// enum obs_encoder_type [obs-encoder.h:44-47]. Deliberately the single names libobs uses; an
// encoder is either an audio codec or a video codec and nothing else.
public enum ObsEncoderType
{
    Audio = 0,
    Video = 1
}

// The capability bits an encoder *type* declares, from obs_encoder_info.caps [obs-encoder.h:35-41].
// Not to be confused with the per-instance flags: these describe what the type can do and are read-
// only.
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

// enum obs_property_type [obs-properties.h:45-60]. The property surface a plugin exposes is what
// this binding can enumerate without guessing at keys: a settings key that does not appear in the
// property list is not a key the plugin will read.
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

// enum obs_combo_format [obs-properties.h:62-67]. For a List property, what each item holds. The
// item accessor has to match: a string item read as an integer returns 0, and vice versa.
public enum ObsComboFormat
{
    Invalid = 0,
    Int,
    Float,
    String,
    Bool
}

// struct obs_encoder_roi [obs-encoder.h:162-176]: the rectangle a caller asks an encoder to
// prioritise, in pixels from the input video's top-left. Priority is −1…1, above 0 asking for more
// quality; encoders without OBS_ENCODER_CAP_ROI ignore the whole call and say so.
public readonly record struct ObsEncoderRoi(uint Top, uint Bottom, uint Left, uint Right, float Priority)
{
    public static ObsEncoderRoi Square(uint x, uint y, uint size, float priority) =>
        new(y, y + size, x, x + size, priority);
}
