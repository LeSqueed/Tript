// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Obs.IntegrationTests;

public sealed class ObsVideoResetTests
{
    // Resolutions, frame rates and formats crossed against each other. Run inside one context
    // because a reset with no output active is exactly the operation being tested — a settings
    // change is meant to be survivable without restarting OBS.
    public static TheoryData<uint, uint, uint, uint, uint, uint, ObsVideoFormat> Matrix()
    {
        var data = new TheoryData<uint, uint, uint, uint, uint, uint, ObsVideoFormat>();

        (uint Base, uint BaseHeight, uint Out, uint OutHeight)[] resolutions =
        [
            (1920, 1080, 1920, 1080),
            (1920, 1080, 1280, 720),
            (3840, 2160, 1920, 1080),
            (1280, 720, 640, 360),
            (1366, 768, 1366, 768)
        ];

        (uint Numerator, uint Denominator)[] rates =
        [
            (30, 1),
            (60, 1),
            (60000, 1001),
            (24000, 1001),
            (144, 1)
        ];

        ObsVideoFormat[] formats =
        [
            ObsVideoFormat.Nv12,
            ObsVideoFormat.I420,
            ObsVideoFormat.I444,
            ObsVideoFormat.Bgra,
            ObsVideoFormat.P010
        ];

        for (var i = 0; i < resolutions.Length; i++)
        {
            var resolution = resolutions[i];
            var rate = rates[i % rates.Length];
            foreach (var format in formats)
                data.Add(resolution.Base, resolution.BaseHeight, resolution.Out, resolution.OutHeight,
                    rate.Numerator, rate.Denominator, format);
        }

        return data;
    }

    [SkippableTheory]
    [MemberData(nameof(Matrix))]
    public void VideoReset_SucceedsAcrossResolutionsRatesAndFormats(uint baseWidth, uint baseHeight, uint outputWidth,
        uint outputHeight, uint fpsNumerator, uint fpsDenominator, ObsVideoFormat format)
    {
        using var session = ObsSession.Start();

        var result = session.Runtime.ResetVideo(new ObsVideoSettings
        {
            BaseWidth = baseWidth,
            BaseHeight = baseHeight,
            OutputWidth = outputWidth,
            OutputHeight = outputHeight,
            FpsNumerator = fpsNumerator,
            FpsDenominator = fpsDenominator,
            OutputFormat = format,
            ColorSpace = format == ObsVideoFormat.P010 ? ObsColorSpace.Rec2100Pq : ObsColorSpace.Rec709
        });

        Assert.Equal(ObsVideoResetResult.Success, result);
        Assert.True(session.Runtime.HasVideo);

        // A success code says the call was accepted, not that it was understood. The read-back and
        // the frame interval are what say the compositor is running at what was asked for. The
        // output dimensions are the exception: libobs quietly rounds them, which the next test pins.
        Assert.True(session.Runtime.TryGetVideoInfo(out var readBack));
        Assert.Equal(baseWidth, readBack!.BaseWidth);
        Assert.Equal(baseHeight, readBack.BaseHeight);
        Assert.Equal(outputWidth & ~3u, readBack.OutputWidth);
        Assert.Equal(outputHeight & ~1u, readBack.OutputHeight);
        Assert.Equal(format, readBack.OutputFormat);

        var expected = (ulong)(1_000_000_000.0 * fpsDenominator / fpsNumerator);
        Assert.InRange(session.Runtime.FrameIntervalNanoseconds, expected - 1, expected + 1);
    }

    // Silently, and still reporting success. 1366x768 is a real laptop panel, so a recorder that
    // trusts the success code and its own settings object will label a 1364-pixel-wide file 1366.
    // The binding does not correct this — it is libobs's behaviour and hiding it would make the
    // read-back disagree with the settings for a different reason — but nothing may be surprised
    // by it either.
    [SkippableTheory]
    [InlineData(1366u, 768u, 1364u, 768u)]
    [InlineData(1922u, 1082u, 1920u, 1082u)]
    [InlineData(1921u, 1081u, 1920u, 1080u)]
    [InlineData(638u, 358u, 636u, 358u)]
    [InlineData(1920u, 1080u, 1920u, 1080u)]
    public void OutputDimensions_AreRoundedDownToMultiplesOfFourAndTwo(
        uint requestedWidth, uint requestedHeight, uint effectiveWidth, uint effectiveHeight)
    {
        using var session = ObsSession.Start();

        var result = session.Runtime.ResetVideo(new ObsVideoSettings
        {
            BaseWidth = 1920,
            BaseHeight = 1080,
            OutputWidth = requestedWidth,
            OutputHeight = requestedHeight
        });

        Assert.Equal(ObsVideoResetResult.Success, result);
        Assert.True(session.Runtime.TryGetVideoInfo(out var readBack));
        Assert.Equal(effectiveWidth, readBack!.OutputWidth);
        Assert.Equal(effectiveHeight, readBack.OutputHeight);
    }

    // The base canvas is left exactly as asked, unlike the output size.
    [SkippableFact]
    public void TheBaseCanvas_IsNotRounded()
    {
        using var session = ObsSession.Start();

        session.ResetVideoOrThrow(new ObsVideoSettings
        {
            BaseWidth = 1366, BaseHeight = 768, OutputWidth = 1280, OutputHeight = 720
        });

        Assert.True(session.Runtime.TryGetVideoInfo(out var readBack));
        Assert.Equal(1366u, readBack!.BaseWidth);
        Assert.Equal(768u, readBack.BaseHeight);
    }

    [SkippableFact]
    public void ASecondReset_ChangesTheRunningFrameRate()
    {
        using var session = ObsSession.Start();

        session.ResetVideoOrThrow(new ObsVideoSettings
        {
            BaseWidth = 1920, BaseHeight = 1080, OutputWidth = 1920, OutputHeight = 1080, FpsNumerator = 60
        });

        session.ResetVideoOrThrow(new ObsVideoSettings
        {
            BaseWidth = 1280, BaseHeight = 720, OutputWidth = 1280, OutputHeight = 720, FpsNumerator = 30
        });

        Assert.Equal(33_333_333ul, session.Runtime.FrameIntervalNanoseconds);
    }

    // The return codes, mapped rather than swallowed. Both of these are states a user reaches:
    // a zero dimension from an unconfigured profile, and a renderer that is not on the machine.
    [SkippableFact]
    public void AZeroDimension_IsReportedAsAnInvalidParameter()
    {
        using var session = ObsSession.Start();

        var result = session.Runtime.ResetVideo(new ObsVideoSettings
        {
            BaseWidth = 0, BaseHeight = 0, OutputWidth = 1920, OutputHeight = 1080
        });

        Assert.Equal(ObsVideoResetResult.InvalidParameter, result);
    }

    [SkippableFact]
    public void AMissingGraphicsModule_IsReportedAsGraphicsModuleNotFound()
    {
        using var session = ObsSession.Start();

        var result = session.Runtime.ResetVideo(new ObsVideoSettings
        {
            GraphicsModule = "libobs-not-a-renderer",
            BaseWidth = 1920, BaseHeight = 1080, OutputWidth = 1920, OutputHeight = 1080
        });

        Assert.Equal(ObsVideoResetResult.GraphicsModuleNotFound, result);
    }

    [SkippableFact]
    public void BeforeTheFirstReset_NoVideoMixExists()
    {
        using var session = ObsSession.Start();

        Assert.False(session.Runtime.HasVideo);
        Assert.False(session.Runtime.IsVideoActive);
        Assert.False(session.Runtime.TryGetVideoInfo(out _));
    }

    [SkippableFact]
    public void EverySetting_ReadsBackAsItWasSet()
    {
        using var session = ObsSession.Start();

        var settings = new ObsVideoSettings
        {
            BaseWidth = 1920,
            BaseHeight = 1080,
            OutputWidth = 1280,
            OutputHeight = 720,
            FpsNumerator = 60000,
            FpsDenominator = 1001,
            OutputFormat = ObsVideoFormat.I444,
            GpuConversion = true,
            ColorSpace = ObsColorSpace.Rec601,
            Range = ObsVideoRange.Full,
            ScaleType = ObsScaleType.Lanczos
        };

        session.ResetVideoOrThrow(settings);

        Assert.True(session.Runtime.TryGetVideoInfo(out var readBack));
        Assert.Equal(settings, readBack);
    }

    // libobs keeps the obs_video_info it was handed, including the graphics module pointer, and
    // never copies the string. Reading it back after the reset call has returned is what proves the
    // binding kept that buffer alive rather than freeing it with the call frame.
    [SkippableFact]
    public void TheGraphicsModuleName_OutlivesTheResetCall()
    {
        using var session = ObsSession.Start();

        session.ResetVideoOrThrow(new ObsVideoSettings
        {
            BaseWidth = 1280, BaseHeight = 720, OutputWidth = 1280, OutputHeight = 720
        });

        // A second reset, so anything scoped to the first call would be long gone.
        session.ResetVideoOrThrow(new ObsVideoSettings
        {
            BaseWidth = 640, BaseHeight = 360, OutputWidth = 640, OutputHeight = 360
        });

        GC.Collect();
        GC.WaitForPendingFinalizers();

        Assert.True(session.Runtime.TryGetVideoInfo(out var readBack));
        Assert.Equal(ObsVideoSettings.DefaultGraphicsModule, readBack!.GraphicsModule);
    }

    [SkippableFact]
    public void AMixWithoutAnOutput_IsPresentButNotActive()
    {
        using var session = ObsSession.Start();

        session.ResetVideoOrThrow(new ObsVideoSettings
        {
            BaseWidth = 1920, BaseHeight = 1080, OutputWidth = 1920, OutputHeight = 1080
        });

        // Active means an output is consuming the mix, not that a mix exists. The distinction is
        // what decides whether a settings change is allowed.
        Assert.True(session.Runtime.HasVideo);
        Assert.False(session.Runtime.IsVideoActive);
    }
}
