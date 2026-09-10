// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Obs.IntegrationTests;

public sealed class ObsVideoResetTests
{
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

        Assert.True(session.Runtime.TryGetVideoInfo(out var readBack));
        Assert.Equal(baseWidth, readBack!.BaseWidth);
        Assert.Equal(baseHeight, readBack.BaseHeight);
        Assert.Equal(outputWidth & ~3u, readBack.OutputWidth);
        Assert.Equal(outputHeight & ~1u, readBack.OutputHeight);
        Assert.Equal(format, readBack.OutputFormat);

        var expected = (ulong)(1_000_000_000.0 * fpsDenominator / fpsNumerator);
        Assert.InRange(session.Runtime.FrameIntervalNanoseconds, expected - 1, expected + 1);
    }

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

    [SkippableFact]
    public void TheGraphicsModuleName_OutlivesTheResetCall()
    {
        using var session = ObsSession.Start();

        session.ResetVideoOrThrow(new ObsVideoSettings
        {
            BaseWidth = 1280, BaseHeight = 720, OutputWidth = 1280, OutputHeight = 720
        });

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

        Assert.True(session.Runtime.HasVideo);
        Assert.False(session.Runtime.IsVideoActive);
    }
}
