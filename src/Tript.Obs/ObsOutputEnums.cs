// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Obs;

// enum obs_output_flags — the capability bits an output *type* declares, from obs_output_info.flags
// [obs-output.h:24-34]. Read per instance with obs_output_get_flags and per type with
// obs_get_output_flags. Describes what the output consumes and what it requires: an encoded output
// takes encoder packets rather than raw media, and a SERVICE output demands an obs_service_t.
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

// The complete failure vocabulary of an output, delivered as the code field of the stop signal and
// as the argument to obs_output_signal_stop [obs-defs.h:37-46]. SUCCESS is not an error; every other
// member is a reason the recording or stream ended. The pairing matters: the code says which failure,
// obs_output_get_last_error says what the plugin had to say about it.
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

// obs_output_set_delay's flags. PRESERVE is the only member; the output keeps recording during a
// delay so a short interruption does not truncate the file.
[Flags]
public enum ObsOutputDelayFlags : uint
{
    None = 0,
    Preserve = 1 << 0
}

// Capacity constants [obs-output.h:36-37]. The audio-track slots and video-track slots an output
// can carry; a slot index beyond these is a programming error that libobs will not check.
public static class ObsOutputCapacity
{
    public const int MaxAudioEncoders = 6;
    public const int MaxVideoEncoders = 10;
}
