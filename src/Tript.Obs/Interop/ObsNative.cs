// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;

namespace Tript.Obs.Interop;

// Every libobs entry point the binding declares, in one place, so the tier-2 symbol test can find
// them by reflection and prove each one resolves against the runtime we actually load.
//
// Two rules hold throughout:
//
//   * Strings in are declared with StringMarshalling.Utf8. libobs is UTF-8 everywhere; the default
//     marshaller is not, and the failure is mojibake rather than an error.
//   * Strings out are nint, never string. The generated marshaller frees a returned string with
//     the COM task allocator, and libobs's strings come from bmem — freeing a borrowed pointer on
//     the wrong heap is a crash, not a leak. Utf8Marshal decides who owns what.
internal static unsafe partial class ObsNative
{
    static ObsNative() => ObsLibrary.Register();

    // ---- util/bmem.h ----

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void bfree(nint pointer);

    // Live bmem allocation count. Diagnostic only, and the one honest way to assert that a
    // startup/shutdown cycle left nothing behind.
    [LibraryImport(ObsLibrary.Name)]
    internal static partial long bnum_allocs();

    // ---- util/base.h ----

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void base_set_log_handler(delegate* unmanaged[Cdecl]<int, nint, nint, nint, void> handler, nint param);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void base_get_log_handler(out nint handler, out nint param);

    // ---- util/dstr.h ----

    // The formatter for the log handler's va_list. Using libobs's own printf keeps the binding off
    // libc, whose shared-object name is not the same on every distribution.
    [LibraryImport(ObsLibrary.Name)]
    internal static partial void dstr_vprintf(DStrNative* destination, nint format, nint arguments);

    // ---- obs.h: startup and shutdown ----

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_startup(string locale, string? moduleConfigPath, nint profilerNameStore);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_shutdown();

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_initialized();

    [LibraryImport(ObsLibrary.Name)]
    internal static partial uint obs_get_version();

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_get_version_string();

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void obs_set_locale(string locale);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_get_locale();

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_wait_for_destroy_queue();

    // ---- obs-nix-platform.h: Linux only, absent from obs.dll ----

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_set_nix_platform(int platform);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial int obs_get_nix_platform();

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_set_nix_platform_display(nint display);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_get_nix_platform_display();

    // ---- obs.h: paths ----

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void obs_add_data_path(string path);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_remove_data_path(string path);

    // Returns an owned string; the caller bfrees it.
    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint obs_find_data_file(string file);

    // ---- obs.h: modules ----

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void obs_add_module_path(string binaryPath, string dataPath);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void obs_add_safe_module(string name);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_load_all_modules2(ref ObsModuleFailureInfoNative failureInfo);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_module_failure_info_free(ref ObsModuleFailureInfoNative failureInfo);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_post_load_modules();

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int obs_open_module(out nint module, string path, string dataPath);

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_init_module(nint module);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_get_module_name(nint module);

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_enum_input_types(nuint index, out nint id);

    // ---- obs.h: video ----

    [LibraryImport(ObsLibrary.Name)]
    internal static partial int obs_reset_video(ref ObsVideoInfoNative videoInfo);

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_get_video_info(ref ObsVideoInfoNative videoInfo);

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_video_active();

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_get_video();

    [LibraryImport(ObsLibrary.Name)]
    internal static partial ulong obs_get_frame_interval_ns();

    // ---- obs.h: audio ----

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_reset_audio(ref ObsAudioInfoNative audioInfo);

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_get_audio_info(ref ObsAudioInfoNative audioInfo);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_get_audio();
}
