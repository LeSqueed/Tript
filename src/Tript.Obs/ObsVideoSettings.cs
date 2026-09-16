// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Obs;

public sealed record ObsVideoSettings
{
    // Cannot change on reset: switching renderers means recreating the OBS context.
    public string GraphicsModule { get; init; } = DefaultGraphicsModule;

    public required uint BaseWidth { get; init; }
    public required uint BaseHeight { get; init; }

    // libobs silently rounds these down (1366 becomes 1364); read them back before reporting.
    public required uint OutputWidth { get; init; }
    public required uint OutputHeight { get; init; }

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

public sealed record ObsAudioSettings
{
    public uint SamplesPerSecond { get; init; } = 48_000;
    public ObsSpeakerLayout Speakers { get; init; } = ObsSpeakerLayout.Stereo;
}
