// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Obs;

// enum video_format, in declaration order. Only a handful are ever asked for as a mix format, but
// the raw-frame callbacks still to come report whatever the source produced, so the full set has
// to be nameable.
public enum ObsVideoFormat
{
    None = 0,
    I420,
    Nv12,
    Yvyu,
    Yuy2,
    Uyvy,
    Rgba,
    Bgra,
    Bgrx,
    Y800,
    I444,
    Bgr3,
    I422,
    I40A,
    I42A,
    Yuva,
    Ayuv,
    I010,
    P010,
    I210,
    I412,
    Ya2L,
    P216,
    P416,
    V210,
    R10L
}

// enum video_colorspace. Default is a request to resolve, not a value.
public enum ObsColorSpace
{
    Default = 0,
    Rec601,
    Rec709,
    SRgb,
    Rec2100Pq,
    Rec2100Hlg
}

// enum video_range_type. Default resolves to Partial for YUV formats and Full otherwise; libobs
// does that with a static inline that is not exported, so a consumer handed Default has been told
// less than the compositor knows.
public enum ObsVideoRange
{
    Default = 0,
    Partial,
    Full
}

// enum obs_scale_type — the compositor's scaler. Note this is not enum video_scale_type from
// media-io, which shares three member names in a different order.
public enum ObsScaleType
{
    Disable = 0,
    Point,
    Bicubic,
    Bilinear,
    Lanczos,
    Area
}

// enum obs_nix_platform_type. Value 0 is deliberately unnamed: it is OBS_NIX_PLATFORM_INVALID on
// the runtimes we target and was a deprecated GLX platform before that — the only value in the
// whole surface whose meaning changed rather than being appended to.
public enum ObsNixPlatform
{
    X11Egl = 1,
    Wayland = 2
}

// obs_reset_video's return codes. Mapped rather than reduced to a bool: "invalid parameter" and
// "the adapter cannot do this" send an application down completely different paths, and the second
// is the one a user will actually hit.
public enum ObsVideoResetResult
{
    Success = 0,
    Failed = -1,
    NotSupported = -2,
    InvalidParameter = -3,
    CurrentlyActive = -4,
    GraphicsModuleNotFound = -5
}
