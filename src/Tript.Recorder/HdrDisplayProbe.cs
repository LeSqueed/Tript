// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Serilog;

namespace Tript.Recorder;

public static partial class HdrDisplayProbe
{
    public static bool AnyDisplayIsHdr()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            return WindowsAnyDisplayIsHdr();
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Debug(exception, "HdrDisplayProbe: no display-config API; assuming SDR.");
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool WindowsAnyDisplayIsHdr()
    {
        if (GetDisplayConfigBufferSizes(QueryOnlyActivePaths, out var pathCount, out var modeCount) != ErrorSuccess)
            return false;

        var paths = new DisplayConfigPathInfo[pathCount];
        var modes = new DisplayConfigModeInfo[modeCount];

        if (QueryDisplayConfig(QueryOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, nint.Zero) != ErrorSuccess)
            return false;

        for (var index = 0; index < pathCount; index++)
        {
            if (IsPathInHdr(paths[index]))
                return true;
        }

        return false;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsPathInHdr(DisplayConfigPathInfo path)
    {
        var request = new DisplayConfigGetAdvancedColorInfo
        {
            Header = new DisplayConfigDeviceInfoHeader
            {
                Type = GetAdvancedColorInfo,
                Size = Marshal.SizeOf<DisplayConfigGetAdvancedColorInfo>(),
                AdapterId = path.TargetInfo.AdapterId,
                Id = path.TargetInfo.Id
            }
        };

        if (DisplayConfigGetDeviceInfo(ref request) != ErrorSuccess)
            return false;

        return (request.Value & AdvancedColorEnabled) != 0;
    }

    internal static IReadOnlyList<(string Name, int Actual, int Expected)> NativeStructSizes() =>
    [
        ("DISPLAYCONFIG_PATH_SOURCE_INFO", Marshal.SizeOf<DisplayConfigPathSourceInfo>(), 20),
        ("DISPLAYCONFIG_PATH_TARGET_INFO", Marshal.SizeOf<DisplayConfigPathTargetInfo>(), 48),
        ("DISPLAYCONFIG_PATH_INFO", Marshal.SizeOf<DisplayConfigPathInfo>(), 72),
        ("DISPLAYCONFIG_DEVICE_INFO_HEADER", Marshal.SizeOf<DisplayConfigDeviceInfoHeader>(), 20),
        ("DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO", Marshal.SizeOf<DisplayConfigGetAdvancedColorInfo>(), 32),
        ("DISPLAYCONFIG_MODE_INFO", Marshal.SizeOf<DisplayConfigModeInfo>(), 64),
    ];

    private const uint QueryOnlyActivePaths = 0x00000002;
    private const int ErrorSuccess = 0;
    private const uint GetAdvancedColorInfo = 9;
    private const uint AdvancedColorEnabled = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;

        public uint RefreshRateNumerator;
        public uint RefreshRateDenominator;

        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo SourceInfo;
        public DisplayConfigPathTargetInfo TargetInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DisplayConfigModeInfo
    {
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigDeviceInfoHeader
    {
        public uint Type;
        public int Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigGetAdvancedColorInfo
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Value;
        public uint ColorEncoding;
        public int BitsPerColorChannel;
    }

    [LibraryImport("user32.dll")]
    private static partial int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [LibraryImport("user32.dll")]
    private static partial int QueryDisplayConfig(
        uint flags,
        ref uint pathCount,
        [Out] DisplayConfigPathInfo[] paths,
        ref uint modeCount,
        [Out] DisplayConfigModeInfo[] modes,
        nint currentTopologyId);

    [LibraryImport("user32.dll")]
    private static partial int DisplayConfigGetDeviceInfo(ref DisplayConfigGetAdvancedColorInfo request);
}
