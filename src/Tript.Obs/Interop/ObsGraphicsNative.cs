// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;

namespace Tript.Obs.Interop;

internal static unsafe partial class ObsNative
{
    internal const int GsColorFormatBgra = 5;
    internal const int GsZStencilNone = 0;
    internal const uint GsTextureRenderTarget = 1 << 2;
    internal const uint GsTextureShared = 1 << 5;
    internal const uint GsClearColor = 1 << 0;
    internal const uint GsInvalidHandle = uint.MaxValue;

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint gs_texture_create(uint width, uint height, int colorFormat, uint levels,
        nint data, uint flags);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void gs_texture_destroy(nint texture);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial uint gs_texture_get_shared_handle(nint texture);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint gs_texrender_create(int colorFormat, int zStencilFormat);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void gs_texrender_destroy(nint texrender);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void gs_texrender_reset(nint texrender);

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool gs_texrender_begin_with_color_space(nint texrender, uint width, uint height,
        int colorSpace);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void gs_texrender_end(nint texrender);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint gs_texrender_get_texture(nint texrender);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void gs_copy_texture(nint destination, nint source);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void gs_ortho(float left, float right, float top, float bottom, float zNear, float zFar);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void gs_clear(uint clearFlags, float* color, float depth, byte stencil);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void gs_blend_state_push();

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void gs_blend_state_pop();

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void gs_enable_blending([MarshalAs(UnmanagedType.U1)] bool enable);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void gs_enable_color([MarshalAs(UnmanagedType.U1)] bool red,
        [MarshalAs(UnmanagedType.U1)] bool green, [MarshalAs(UnmanagedType.U1)] bool blue,
        [MarshalAs(UnmanagedType.U1)] bool alpha);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void gs_flush();

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void gs_enum_adapters(delegate* unmanaged[Cdecl]<nint, nint, uint, byte> callback,
        nint param);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_add_main_rendered_callback(delegate* unmanaged[Cdecl]<nint, void> rendered,
        nint param);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_remove_main_rendered_callback(delegate* unmanaged[Cdecl]<nint, void> rendered,
        nint param);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_source_video_render(nint source);
}
