// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;

namespace Tript.Obs.Interop;

// Only structs libobs declares in its public API are mirrored here. Its internal layouts are
// reached exclusively through opaque pointers, which is what lets one binding serve a runtime
// range without version branching.

// struct obs_video_info. 56 bytes; the one-byte C bool at GpuConversion is followed by three bytes
// of padding, which natural alignment reproduces — a managed bool would marshal as four bytes and
// shift every field after it.
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

// struct obs_audio_info.
[StructLayout(LayoutKind.Sequential)]
internal struct ObsAudioInfoNative
{
    public uint SamplesPerSecond;
    public int Speakers;
}

// struct obs_module_failure_info. The array and its strings are libobs's; release with
// obs_module_failure_info_free rather than bfree.
[StructLayout(LayoutKind.Sequential)]
internal struct ObsModuleFailureInfoNative
{
    public nint FailedModules;
    public nuint Count;
}

// struct dstr, libobs's growable string. Its buffer comes from bmem, so the caller frees it with
// bfree — dstr_free is a static inline and therefore not exported.
[StructLayout(LayoutKind.Sequential)]
internal struct DStrNative
{
    public nint Array;
    public nuint Length;
    public nuint Capacity;
}
