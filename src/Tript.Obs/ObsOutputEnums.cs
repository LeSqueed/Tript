// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Obs;

[Flags]
public enum ObsOutputFlags : uint
{
    None = 0,
    Video = 1 << 0,
    Audio = 1 << 1,
    Av = Video | Audio,
    Encoded = 1 << 2,
    Service = 1 << 3,
    MultiTrack = 1 << 4,
    CanPause = 1 << 5,
    MultiTrackVideo = 1 << 6,
    MultiTrackAv = MultiTrack | MultiTrackVideo
}

public enum ObsOutputStopCode
{
    Success = 0,
    BadPath = -1,
    ConnectFailed = -2,
    InvalidStream = -3,
    Error = -4,
    Disconnected = -5,
    Unsupported = -6,
    NoSpace = -7,
    EncodeError = -8,
    HdrDisabled = -9
}

[Flags]
public enum ObsOutputDelayFlags : uint
{
    None = 0,
    Preserve = 1 << 0
}

public static class ObsOutputCapacity
{
    public const int MaxAudioEncoders = 6;
    public const int MaxVideoEncoders = 10;
}
