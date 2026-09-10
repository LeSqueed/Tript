// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;

namespace Tript.Obs.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct ObsVideoInfoNative
{
    public nint GraphicsModule;
    public uint FpsNumerator;
    public uint FpsDenominator;
    public uint BaseWidth;
    public uint BaseHeight;
    public uint OutputWidth;
    public uint OutputHeight;
    public int OutputFormat;
    public uint Adapter;
    public byte GpuConversion;
    public int ColorSpace;
    public int Range;
    public int ScaleType;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ObsAudioInfoNative
{
    public uint SamplesPerSecond;
    public int Speakers;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ObsModuleFailureInfoNative
{
    public nint FailedModules;
    public nuint Count;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Vec2Native
{
    public float X;
    public float Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ObsSceneItemCropNative
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ObsTransformInfoNative
{
    public Vec2Native Position;
    public float Rotation;
    public Vec2Native Scale;
    public uint Alignment;
    public int BoundsType;
    public uint BoundsAlignment;
    public Vec2Native Bounds;
    public byte CropToBounds;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DStrNative
{
    public nint Array;
    public nuint Length;
    public nuint Capacity;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ObsEncoderRoiNative
{
    public uint Top;
    public uint Bottom;
    public uint Left;
    public uint Right;
    public float Priority;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VideoDataNative
{
    public fixed long Data[8];
    public fixed uint Linesize[8];
    public ulong Timestamp;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VideoScaleInfoNative
{
    public int Format;
    public uint Width;
    public uint Height;
    public int Range;
    public int Colorspace;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VideoOutputInfoNative
{
    public nint Name;
    public int Format;
    public uint FpsNumerator;
    public uint FpsDenominator;
    public uint Width;
    public uint Height;
    public nuint CacheSize;
    public int ColorSpace;
    public int Range;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CalldataNative
{
    public nint Stack;
    public nuint Size;
    public nuint Capacity;
    public byte Fixed;
}
