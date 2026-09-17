// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Tript.Obs.Interop;
using Xunit;

namespace Tript.Obs.IntegrationTests;

public sealed class ObsInteropTests
{
    [SkippableFact]
    public void TheObsRuntime_LoadsToASingleHandle()
    {
        ObsTestEnvironment.RequireUsableRuntime();

        var handle = ObsLibrary.EnsureLoaded();

        Assert.NotEqual(nint.Zero, handle);
        Assert.Equal(handle, ObsLibrary.EnsureLoaded());
    }

    [SkippableFact]
    public void ChangingTheRuntimeDirectory_IsRefusedOnceTheLibraryIsLoaded()
    {
        ObsTestEnvironment.RequireUsableRuntime();

        ObsLibrary.EnsureLoaded();

        Assert.Throws<InvalidOperationException>(() => ObsRuntime.SetRuntimeDirectory("/nonexistent/obs-runtime"));
    }

    [SkippableFact]
    public void TheLoadedRuntime_MeetsTheSupportedMinimumVersion()
    {
        ObsTestEnvironment.RequireUsableRuntime();

        var version = ObsRuntime.Version;

        Assert.StartsWith($"{version.Major}.{version.Minor}.", ObsRuntime.VersionString, StringComparison.Ordinal);

        Assert.True(version >= new Version(30, 1),
            $"OBS {ObsRuntime.VersionString} is below the supported 30.1 minimum.");
    }

    [SkippableFact]
    public void EveryDeclaredEntryPoint_ResolvesAgainstTheLoadedRuntime()
    {
        ObsTestEnvironment.RequireUsableRuntime();

        var handle = ObsLibrary.EnsureLoaded();
        var declarations = DeclaredEntryPoints().ToArray();

        Assert.True(declarations.Length >= 30,
            $"Expected the binding to declare at least 30 libobs entry points, found {declarations.Length}.");

        var unresolved = declarations
            .Where(entryPoint => !IsPlatformSpecific(entryPoint))
            .Where(entryPoint => !NativeLibrary.TryGetExport(handle, entryPoint, out _))
            .ToArray();

        Assert.True(unresolved.Length == 0,
            $"These symbols are declared by the binding but not exported by the loaded OBS runtime: {string.Join(", ", unresolved)}");
    }

    [Fact]
    public void EveryDeclaredEntryPoint_TargetsTheResolvedLibrary()
    {
        var strays = typeof(ObsNative)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Select(method => method.GetCustomAttribute<LibraryImportAttribute>())
            .Where(attribute => attribute is not null && attribute.LibraryName != ObsLibrary.Name)
            .Select(attribute => attribute!.LibraryName)
            .Distinct()
            .ToArray();

        Assert.Empty(strays);
    }

    [Fact]
    public void ObsVideoInfo_MatchesTheNativeStructLayout()
    {
        Assert.Equal(56, Marshal.SizeOf<ObsVideoInfoNative>());

        Assert.Equal(0, (int)Marshal.OffsetOf<ObsVideoInfoNative>(nameof(ObsVideoInfoNative.GraphicsModule)));
        Assert.Equal(8, (int)Marshal.OffsetOf<ObsVideoInfoNative>(nameof(ObsVideoInfoNative.FpsNumerator)));
        Assert.Equal(16, (int)Marshal.OffsetOf<ObsVideoInfoNative>(nameof(ObsVideoInfoNative.BaseWidth)));
        Assert.Equal(24, (int)Marshal.OffsetOf<ObsVideoInfoNative>(nameof(ObsVideoInfoNative.OutputWidth)));
        Assert.Equal(32, (int)Marshal.OffsetOf<ObsVideoInfoNative>(nameof(ObsVideoInfoNative.OutputFormat)));
        Assert.Equal(36, (int)Marshal.OffsetOf<ObsVideoInfoNative>(nameof(ObsVideoInfoNative.Adapter)));

        Assert.Equal(40, (int)Marshal.OffsetOf<ObsVideoInfoNative>(nameof(ObsVideoInfoNative.GpuConversion)));
        Assert.Equal(44, (int)Marshal.OffsetOf<ObsVideoInfoNative>(nameof(ObsVideoInfoNative.ColorSpace)));
        Assert.Equal(48, (int)Marshal.OffsetOf<ObsVideoInfoNative>(nameof(ObsVideoInfoNative.Range)));
        Assert.Equal(52, (int)Marshal.OffsetOf<ObsVideoInfoNative>(nameof(ObsVideoInfoNative.ScaleType)));
    }

    [Fact]
    public void ObsAudioInfo_MatchesTheNativeStructLayout()
    {
        Assert.Equal(8, Marshal.SizeOf<ObsAudioInfoNative>());
        Assert.Equal(4, (int)Marshal.OffsetOf<ObsAudioInfoNative>(nameof(ObsAudioInfoNative.Speakers)));
    }

    [Fact]
    public void ObsTransformInfo_MatchesTheNativeStructLayout()
    {
        Assert.Equal(8, Marshal.SizeOf<Vec2Native>());
        Assert.Equal(16, Marshal.SizeOf<ObsSceneItemCropNative>());
        Assert.Equal(44, Marshal.SizeOf<ObsTransformInfoNative>());

        Assert.Equal(0, (int)Marshal.OffsetOf<ObsTransformInfoNative>(nameof(ObsTransformInfoNative.Position)));
        Assert.Equal(8, (int)Marshal.OffsetOf<ObsTransformInfoNative>(nameof(ObsTransformInfoNative.Rotation)));
        Assert.Equal(12, (int)Marshal.OffsetOf<ObsTransformInfoNative>(nameof(ObsTransformInfoNative.Scale)));
        Assert.Equal(20, (int)Marshal.OffsetOf<ObsTransformInfoNative>(nameof(ObsTransformInfoNative.Alignment)));
        Assert.Equal(24, (int)Marshal.OffsetOf<ObsTransformInfoNative>(nameof(ObsTransformInfoNative.BoundsType)));
        Assert.Equal(28, (int)Marshal.OffsetOf<ObsTransformInfoNative>(nameof(ObsTransformInfoNative.BoundsAlignment)));
        Assert.Equal(32, (int)Marshal.OffsetOf<ObsTransformInfoNative>(nameof(ObsTransformInfoNative.Bounds)));

        Assert.Equal(40, (int)Marshal.OffsetOf<ObsTransformInfoNative>(nameof(ObsTransformInfoNative.CropToBounds)));
    }

    [Fact]
    public unsafe void VideoIoStructs_MatchTheNativeLayout()
    {
        Assert.Equal(104, Marshal.SizeOf<VideoDataNative>());
        Assert.Equal(0, (int)Marshal.OffsetOf<VideoDataNative>(nameof(VideoDataNative.Data)));
        Assert.Equal(64, (int)Marshal.OffsetOf<VideoDataNative>(nameof(VideoDataNative.Linesize)));
        Assert.Equal(96, (int)Marshal.OffsetOf<VideoDataNative>(nameof(VideoDataNative.Timestamp)));

        Assert.Equal(20, Marshal.SizeOf<VideoScaleInfoNative>());
        Assert.Equal(0, (int)Marshal.OffsetOf<VideoScaleInfoNative>(nameof(VideoScaleInfoNative.Format)));
        Assert.Equal(4, (int)Marshal.OffsetOf<VideoScaleInfoNative>(nameof(VideoScaleInfoNative.Width)));
        Assert.Equal(8, (int)Marshal.OffsetOf<VideoScaleInfoNative>(nameof(VideoScaleInfoNative.Height)));
        Assert.Equal(12, (int)Marshal.OffsetOf<VideoScaleInfoNative>(nameof(VideoScaleInfoNative.Range)));
        Assert.Equal(16, (int)Marshal.OffsetOf<VideoScaleInfoNative>(nameof(VideoScaleInfoNative.Colorspace)));

        Assert.Equal(48, Marshal.SizeOf<VideoOutputInfoNative>());
        Assert.Equal(0, (int)Marshal.OffsetOf<VideoOutputInfoNative>(nameof(VideoOutputInfoNative.Name)));
        Assert.Equal(8, (int)Marshal.OffsetOf<VideoOutputInfoNative>(nameof(VideoOutputInfoNative.Format)));
        Assert.Equal(12, (int)Marshal.OffsetOf<VideoOutputInfoNative>(nameof(VideoOutputInfoNative.FpsNumerator)));
        Assert.Equal(16, (int)Marshal.OffsetOf<VideoOutputInfoNative>(nameof(VideoOutputInfoNative.FpsDenominator)));
        Assert.Equal(20, (int)Marshal.OffsetOf<VideoOutputInfoNative>(nameof(VideoOutputInfoNative.Width)));
        Assert.Equal(24, (int)Marshal.OffsetOf<VideoOutputInfoNative>(nameof(VideoOutputInfoNative.Height)));
        Assert.Equal(32, (int)Marshal.OffsetOf<VideoOutputInfoNative>(nameof(VideoOutputInfoNative.CacheSize)));
        Assert.Equal(40, (int)Marshal.OffsetOf<VideoOutputInfoNative>(nameof(VideoOutputInfoNative.ColorSpace)));
        Assert.Equal(44, (int)Marshal.OffsetOf<VideoOutputInfoNative>(nameof(VideoOutputInfoNative.Range)));

        Assert.Equal(20, Marshal.SizeOf<ObsEncoderRoiNative>());
        Assert.Equal(16, (int)Marshal.OffsetOf<ObsEncoderRoiNative>(nameof(ObsEncoderRoiNative.Priority)));
    }

    [Fact]
    public void VaList_MatchesTheSystemVStructLayout()
    {
        Assert.Equal(24, Marshal.SizeOf<VaListSystemV>());
        Assert.Equal(4, (int)Marshal.OffsetOf<VaListSystemV>(nameof(VaListSystemV.FpOffset)));
        Assert.Equal(8, (int)Marshal.OffsetOf<VaListSystemV>(nameof(VaListSystemV.OverflowArgArea)));
        Assert.Equal(16, (int)Marshal.OffsetOf<VaListSystemV>(nameof(VaListSystemV.RegSaveArea)));
    }

    [SkippableFact]
    public unsafe void AnOwnedString_RoundTripsByteIdenticallyAndIsFreedOnLibobsHeap()
    {
        ObsTestEnvironment.RequireUsableRuntime();

        const string original = "café — 日本語 — Ω — 🎮 — ünïcödé";
        var expected = Encoding.UTF8.GetBytes(original);

        var bmemdup = (delegate* unmanaged[Cdecl]<nint, nuint, nint>)
            NativeLibrary.GetExport(ObsLibrary.EnsureLoaded(), "bmemdup");

        var native = Utf8Marshal.Allocate(original);
        try
        {
            var copied = bmemdup(native, (nuint)expected.Length + 1);
            Assert.NotEqual(nint.Zero, copied);

            var actual = new byte[expected.Length];
            Marshal.Copy(copied, actual, 0, expected.Length);
            Assert.Equal(expected, actual);

            var before = ObsRuntime.LiveAllocationCount;
            Assert.Equal(original, Utf8Marshal.ReadOwned(copied));
            Assert.Equal(before - 1, ObsRuntime.LiveAllocationCount);
        }
        finally
        {
            Utf8Marshal.Free(native);
        }
    }

    private static bool IsPlatformSpecific(string entryPoint) =>
        OperatingSystem.IsWindows()
            ? entryPoint.Contains("nix_platform", StringComparison.Ordinal)
            : entryPoint.StartsWith("gs_duplicator_", StringComparison.Ordinal)
                || entryPoint == "gs_texture_get_shared_handle";

    private static IEnumerable<string> DeclaredEntryPoints() =>
        typeof(ObsNative)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Select(method => (Method: method, Import: method.GetCustomAttribute<LibraryImportAttribute>()))
            .Where(entry => entry.Import is not null && entry.Import.LibraryName == ObsLibrary.Name)
            .Select(entry => entry.Import!.EntryPoint ?? entry.Method.Name)
            .Distinct();
}
