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

    // ---- obs-data.h: settings objects ----
    //
    // Every numeric setting is long long. Narrowing it to int works right up until a bitrate,
    // a timestamp or a file size does not fit, and the loss is silent.
    //
    // The autoselect family is deprecated in 32.2.1 and deliberately absent here.

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_data_create();

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint obs_data_create_from_json(string json);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint obs_data_create_from_json_file(string file);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint obs_data_create_from_json_file_safe(string file, string backupExtension);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_data_addref(nint data);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_data_release(nint data);

    // ---- obs-data.h: setters ----

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void obs_data_set_string(nint data, string name, string? value);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void obs_data_set_int(nint data, string name, long value);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void obs_data_set_double(nint data, string name, double value);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void obs_data_set_bool(nint data, string name, [MarshalAs(UnmanagedType.U1)] bool value);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void obs_data_set_obj(nint data, string name, nint value);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void obs_data_set_array(nint data, string name, nint value);

    // ---- obs-data.h: getters ----

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint obs_data_get_string(nint data, string name);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial long obs_data_get_int(nint data, string name);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial double obs_data_get_double(nint data, string name);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_data_get_bool(nint data, string name);

    // Incremented; the caller releases.
    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint obs_data_get_obj(nint data, string name);

    // Incremented; the caller releases.
    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint obs_data_get_array(nint data, string name);

    // ---- obs-data.h: defaults ----

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void obs_data_set_default_string(nint data, string name, string? value);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void obs_data_set_default_int(nint data, string name, long value);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void obs_data_set_default_double(nint data, string name, double value);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void obs_data_set_default_bool(nint data, string name, [MarshalAs(UnmanagedType.U1)] bool value);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void obs_data_set_default_obj(nint data, string name, nint value);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void obs_data_set_default_array(nint data, string name, nint value);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint obs_data_get_default_string(nint data, string name);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial long obs_data_get_default_int(nint data, string name);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial double obs_data_get_default_double(nint data, string name);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_data_get_default_bool(nint data, string name);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint obs_data_get_default_obj(nint data, string name);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint obs_data_get_default_array(nint data, string name);

    // Incremented; the caller releases.
    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_data_get_defaults(nint data);

    // ---- obs-data.h: presence, clearing, merging ----

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_data_has_user_value(nint data, string name);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_data_has_default_value(nint data, string name);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void obs_data_erase(nint data, string name);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_data_clear(nint data);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void obs_data_unset_user_value(nint data, string name);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void obs_data_unset_default_value(nint data, string name);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_data_apply(nint target, nint applyData);

    // ---- obs-data.h: serialisation ----

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_data_get_json(nint data);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_data_get_json_with_defaults(nint data);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_data_get_json_pretty(nint data);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_data_get_json_pretty_with_defaults(nint data);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_data_save_json(nint data, string file);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_data_save_json_safe(nint data, string file, string tempExtension, string backupExtension);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_data_save_json_pretty_safe(nint data, string file, string tempExtension, string backupExtension);

    // ---- obs-data.h: iteration ----

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_data_first(nint data);

    // Takes obs_data_item_t **: it releases the current item and overwrites the caller's variable.
    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_data_item_next(ref nint item);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_data_item_release(ref nint item);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_data_item_get_name(nint item);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial int obs_data_item_gettype(nint item);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial int obs_data_item_numtype(nint item);

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_data_item_has_user_value(nint item);

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_data_item_has_default_value(nint item);

    // ---- obs-data.h: arrays ----

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_data_array_create();

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_data_array_addref(nint array);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_data_array_release(nint array);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nuint obs_data_array_count(nint array);

    // Incremented; the caller releases.
    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_data_array_item(nint array, nuint index);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nuint obs_data_array_push_back(nint array, nint item);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_data_array_insert(nint array, nuint index, nint item);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_data_array_push_back_array(nint array, nint other);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_data_array_erase(nint array, nuint index);
}
