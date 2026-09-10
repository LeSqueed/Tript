// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Obs;

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

public enum ObsColorSpace
{
    Default = 0,
    Rec601,
    Rec709,
    SRgb,
    Rec2100Pq,
    Rec2100Hlg
}

public enum ObsSourceColorSpace
{
    Srgb = 0,
    Srgb16F,
    Extended709,
    Scrgb709
}

public enum ObsVideoRange
{
    Default = 0,
    Partial,
    Full
}

public enum ObsScaleType
{
    Disable = 0,
    Point,
    Bicubic,
    Bilinear,
    Lanczos,
    Area
}

public enum ObsNixPlatform
{
    X11Egl = 1,
    Wayland = 2
}

public enum ObsVideoResetResult
{
    Success = 0,
    Failed = -1,
    NotSupported = -2,
    InvalidParameter = -3,
    CurrentlyActive = -4,
    GraphicsModuleNotFound = -5
}
