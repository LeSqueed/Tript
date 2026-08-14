// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Obs;

// struct obs_video_info, as something an application can hold and compare. The four dimensions are
// required because their zero default is the one combination libobs rejects outright, and a caller
// that forgot to set them should not discover it as a runtime error code.
public sealed record ObsVideoSettings
{
    // "libobs-opengl" or "libobs-d3d11". Unlike every other field this one cannot be changed by a
    // later reset — switching renderers means destroying and recreating the whole OBS context.
    public string GraphicsModule { get; init; } = DefaultGraphicsModule;

    public required uint BaseWidth { get; init; }
    public required uint BaseHeight { get; init; }

    // libobs rounds these down to a multiple of four and a multiple of two respectively, silently
    // and while still reporting success — 1366 becomes 1364. Read them back with TryGetVideoInfo
    // before telling a user what resolution they are recording at.
    public required uint OutputWidth { get; init; }
    public required uint OutputHeight { get; init; }

    // A fraction, because the rates that matter are not integers: 59.94 fps is 60000/1001.
    public uint FpsNumerator { get; init; } = 60;
    public uint FpsDenominator { get; init; } = 1;

    public ObsVideoFormat OutputFormat { get; init; } = ObsVideoFormat.Nv12;
    public uint Adapter { get; init; }
    public bool GpuConversion { get; init; } = true;
    public ObsColorSpace ColorSpace { get; init; } = ObsColorSpace.Rec709;
    public ObsVideoRange Range { get; init; } = ObsVideoRange.Partial;
    public ObsScaleType ScaleType { get; init; } = ObsScaleType.Bicubic;

    public static string DefaultGraphicsModule =>
        OperatingSystem.IsWindows() ? "libobs-d3d11" : "libobs-opengl";
}

// struct obs_audio_info. obs_reset_audio2 adds buffering controls; the plain form is bound because
// nothing in the recorder needs to tune buffering, and the smaller surface is the one that survives
// a runtime change.
public sealed record ObsAudioSettings
{
    public uint SamplesPerSecond { get; init; } = 48_000;
    public ObsSpeakerLayout Speakers { get; init; } = ObsSpeakerLayout.Stereo;
}
