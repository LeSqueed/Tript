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

    // Incremented; the caller releases. Null for an id no module registered.
    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint obs_get_source_defaults(string id);

    // Returns an obs_properties_t; free with obs_properties_destroy. Null for an id that is not
    // registered or declares no properties.
    //
    // This calls the plugin's own get_properties with a NULL instance pointer, and whether that is
    // survivable is the plugin's business, not libobs's — there is no way to ask in advance. A
    // well-behaved builder tests the pointer (image-source opens with `if (s && ...)`);
    // linux-capture's xshm_properties does not, and reads data->source straight through the NULL.
    // Measured in plain C against the system libobs, and still the case in 32.2.2. Use
    // obs_source_properties for anything whose builder might want its instance.
    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint obs_get_source_properties(string id);

    // Returns an obs_properties_t for a *created* source; free with obs_properties_destroy. Some
    // source types build a different property list for an instance than for the type-level probe,
    // and for the capture sources this is the only route that is safe at all: the instance is
    // exactly what their builders dereference. See obs_get_source_properties above.
    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_source_properties(nint source);

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

    // ---- media-io/video-io.h: raw frame subscription ----
    //
    // The media-io pair, not the core obs_add_raw_video_callback2: connect2 reports failure (an
    // unsatisfiable conversion, a duplicate (callback, param) pair, or a zero frame-rate divisor)
    // where the core function silently discards it. disconnect is void because disconnect2, which
    // reports whether the input was found, only exists from 31.1.2 — and that boolean would not be
    // a quiescence guarantee anyway. The callback signature is the header's
    // void (*)(void *param, struct video_data *frame).

    // Returns false when the conversion cannot be satisfied, when (callback, param) is already
    // connected, or when frameRateDivisor is zero. The frame_rate_divisor is counted per
    // subscription and delivers every Nth composited frame; the skipped frames are dropped before
    // any conversion.
    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool video_output_connect2(
        nint video, in VideoScaleInfoNative conversion, uint frameRateDivisor,
        delegate* unmanaged[Cdecl]<nint, nint, void> callback, nint param);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void video_output_disconnect(
        nint video, delegate* unmanaged[Cdecl]<nint, nint, void> callback, nint param);

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool video_output_active(nint video);

    // Borrowed; lives for the lifetime of the video output.
    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint video_output_get_info(nint video);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial int video_output_get_format(nint video);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial uint video_output_get_width(nint video);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial uint video_output_get_height(nint video);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial double video_output_get_frame_rate(nint video);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial uint video_output_get_skipped_frames(nint video);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial uint video_output_get_total_frames(nint video);

    // ---- obs.h: audio ----

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_reset_audio(ref ObsAudioInfoNative audioInfo);

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_get_audio_info(ref ObsAudioInfoNative audioInfo);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_get_audio();

    // ---- obs.h: output channels ----

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_set_output_source(uint channel, nint source);

    // Incremented; the caller releases.
    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_get_output_source(uint channel);

    // ---- obs.h: sources ----
    //
    // obs_source_create copies both the id and the name — measured, and the opposite of
    // obs_reset_video, which keeps the caller's pointer. Nothing here has to be kept alive.

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint obs_source_create(string id, string name, nint settings, nint hotkeyData);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint obs_source_create_private(string id, string name, nint settings);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_source_release(nint source);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_source_get_ref(nint source);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_source_remove(nint source);

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_source_removed(nint source);

    // Incremented; the caller releases.
    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint obs_get_source_by_name(string name);

    // Null for an id no loaded module registered, which is the only reliable way to tell: creating
    // an unregistered id succeeds and hands back a placeholder rather than null.
    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint obs_source_get_display_name(string id);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial uint obs_get_source_output_flags(string id);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial uint obs_source_get_output_flags(nint source);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_source_get_name(nint source);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void obs_source_set_name(nint source, string name);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_source_get_uuid(nint source);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_source_get_id(nint source);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_source_get_unversioned_id(nint source);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial int obs_source_get_type(nint source);

    // Incremented; the caller releases. Note this is the very object handed to obs_source_create,
    // not a copy of it.
    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_source_get_settings(nint source);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_source_update(nint source, nint settings);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_source_reset_settings(nint source, nint settings);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial uint obs_source_get_width(nint source);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial uint obs_source_get_height(nint source);

    // The two nit levels the compositor converts between SDR and HDR with. obs_reset_video does not
    // set them, so an application that never calls obs_set_video_levels gets whatever the process
    // started with.
    // The duplicator is the one thing on Windows that reports a display's real colour state, and it
    // reports it before any output exists. Requires the graphics context.
    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint gs_duplicator_create(int monitorIndex);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void gs_duplicator_destroy(nint duplicator);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial int gs_duplicator_get_color_space(nint duplicator);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial float gs_duplicator_get_sdr_white_level(nint duplicator);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_enter_graphics();

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_leave_graphics();

    [LibraryImport(ObsLibrary.Name)]
    internal static partial float obs_get_video_sdr_white_level();

    [LibraryImport(ObsLibrary.Name)]
    internal static partial float obs_get_video_hdr_nominal_peak_level();

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_set_video_levels(float sdrWhiteLevel, float hdrNominalPeakLevel);

    // The preferred-spaces argument is a hint for sources that can produce more than one; passing
    // none asks for what the source actually has, which is the question here.
    [LibraryImport(ObsLibrary.Name)]
    internal static partial int obs_source_get_color_space(
        nint source, nuint count, [In] int[]? preferredSpaces);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial uint obs_source_get_base_width(nint source);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial uint obs_source_get_base_height(nint source);

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_source_enabled(nint source);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_source_set_enabled(nint source, [MarshalAs(UnmanagedType.U1)] bool enabled);

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_source_active(nint source);

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_source_showing(nint source);

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_source_is_scene(nint source);

    // ---- obs.h: source audio routing ----
    //
    // The per-source audio controls behind the multi-track routing. A source
    // declares which mixers it feeds with a bitmask — bit n means mixer n, and MAX_AUDIO_MIXES is
    // 6 — and its per-source volume with a linear multiplier. The active pair is what makes a
    // capture source actually produce audio: libobs only runs a source's audio while its active
    // reference count is non-zero.

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_source_set_audio_mixers(nint source, uint mixers);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial uint obs_source_get_audio_mixers(nint source);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_source_set_volume(nint source, float volume);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial float obs_source_get_volume(nint source);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_source_inc_active(nint source);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_source_dec_active(nint source);

    // ---- obs.h: weak source references ----
    //
    // The weak control block is bmem's and outlives obs_shutdown, so it is released like an
    // obs_data rather than like the source it points at.

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_source_get_weak_source(nint source);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_weak_source_release(nint weak);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_weak_source_get_source(nint weak);

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_weak_source_expired(nint weak);

    // ---- obs.h: scenes ----

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint obs_scene_create(string name);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint obs_scene_create_private(string name);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_scene_release(nint scene);

    // Neither conversion changes a reference count.
    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_scene_get_source(nint scene);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_scene_from_source(nint source);

    // Borrowed: the item belongs to the scene, which is why every handle in this binding takes its
    // own reference before storing one.
    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_scene_add(nint scene, nint source);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint obs_scene_find_source(nint scene, string name);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_scene_find_sceneitem_by_id(nint scene, long id);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_scene_enum_items(
        nint scene, delegate* unmanaged[Cdecl]<nint, nint, nint, byte> callback, nint parameter);

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_scene_reorder_items(nint scene, nint* itemOrder, nuint count);

    // ---- obs.h: scene items ----

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_sceneitem_addref(nint item);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_sceneitem_release(nint item);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_sceneitem_remove(nint item);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial long obs_sceneitem_get_id(nint item);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_sceneitem_get_scene(nint item);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_sceneitem_get_source(nint item);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_sceneitem_set_pos(nint item, ref Vec2Native position);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_sceneitem_get_pos(nint item, out Vec2Native position);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_sceneitem_set_rot(nint item, float degrees);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial float obs_sceneitem_get_rot(nint item);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_sceneitem_set_scale(nint item, ref Vec2Native scale);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_sceneitem_get_scale(nint item, out Vec2Native scale);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_sceneitem_set_alignment(nint item, uint alignment);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial uint obs_sceneitem_get_alignment(nint item);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_sceneitem_set_bounds_type(nint item, int type);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial int obs_sceneitem_get_bounds_type(nint item);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_sceneitem_set_bounds_alignment(nint item, uint alignment);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial uint obs_sceneitem_get_bounds_alignment(nint item);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_sceneitem_set_bounds(nint item, ref Vec2Native bounds);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_sceneitem_get_bounds(nint item, out Vec2Native bounds);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_sceneitem_set_bounds_crop(nint item, [MarshalAs(UnmanagedType.U1)] bool crop);

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_sceneitem_get_bounds_crop(nint item);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_sceneitem_set_crop(nint item, ref ObsSceneItemCropNative crop);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_sceneitem_get_crop(nint item, out ObsSceneItemCropNative crop);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_sceneitem_get_info2(nint item, out ObsTransformInfoNative info);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_sceneitem_set_info2(nint item, ref ObsTransformInfoNative info);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_sceneitem_set_order(nint item, int movement);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_sceneitem_set_order_position(nint item, int position);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial int obs_sceneitem_get_order_position(nint item);

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_sceneitem_visible(nint item);

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_sceneitem_set_visible(nint item, [MarshalAs(UnmanagedType.U1)] bool visible);

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_sceneitem_locked(nint item);

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_sceneitem_set_locked(nint item, [MarshalAs(UnmanagedType.U1)] bool locked);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_sceneitem_select(nint item, [MarshalAs(UnmanagedType.U1)] bool select);

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_sceneitem_selected(nint item);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_sceneitem_set_scale_filter(nint item, int filter);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial int obs_sceneitem_get_scale_filter(nint item);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_sceneitem_set_blending_method(nint item, int method);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial int obs_sceneitem_get_blending_method(nint item);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_sceneitem_set_blending_mode(nint item, int mode);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial int obs_sceneitem_get_blending_mode(nint item);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_sceneitem_defer_update_begin(nint item);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_sceneitem_defer_update_end(nint item);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_sceneitem_force_update_transform(nint item);

    // ---- obs.h: encoders ----
    //
    // Both create functions hand back a non-null placeholder for an unregistered id — measured, and
    // the reason the binding probes obs_get_encoder_codec first. The id and name are copied, like
    // obs_source_create. The settings object is retained, not copied.

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint obs_video_encoder_create(string id, string name, nint settings, nint hotkeyData);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint obs_audio_encoder_create(string id, string name, nint settings, nuint mixerIndex, nint hotkeyData);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_encoder_release(nint encoder);

    // Takes a strong reference and hands the same pointer back, or null when the encoder is already
    // being destroyed. obs_encoder_addref is the deprecated spelling and is deliberately absent.
    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_encoder_get_ref(nint encoder);

    // Borrowed.
    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_encoder_get_name(nint encoder);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void obs_encoder_set_name(nint encoder, string name);

    // Borrowed.
    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_encoder_get_id(nint encoder);

    // Borrowed.
    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_encoder_get_codec(nint encoder);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial int obs_encoder_get_type(nint encoder);

    // The type-level probes. obs_get_encoder_codec returns NULL for an id no module registered, and
    // that is the reliable unavailability test: obs_get_encoder_type returns Audio (0) for an
    // unknown id, measured, so it cannot stand in.
    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint obs_get_encoder_codec(string id);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int obs_get_encoder_type(string id);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial uint obs_get_encoder_caps(string id);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial uint obs_encoder_get_caps(nint encoder);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_enum_encoder_types(nuint index, out nint id);

    // Incremented; the caller releases. Null for an id no module registered.
    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint obs_encoder_defaults(string id);

    // Incremented; the caller releases. Note the settings object is *shared*, not copied — an edit
    // through the caller's reference reaches the encoder without an Update call.
    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_encoder_get_settings(nint encoder);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_encoder_update(nint encoder, nint settings);

    // Returns an obs_properties_t; free with obs_properties_destroy. Null for an id that is not
    // registered or declares no properties.
    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint obs_get_encoder_properties(string id);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_properties_destroy(nint props);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_properties_first(nint props);

    // Takes obs_property_t **: the current item is released and the caller's variable overwritten.
    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_property_next(ref nint property);

    // Borrowed.
    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_property_name(nint property);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial int obs_property_get_type(nint property);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial int obs_property_list_format(nint property);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nuint obs_property_list_item_count(nint property);

    // Borrowed.
    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_property_list_item_name(nint property, nuint index);

    // Borrowed.
    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_property_list_item_string(nint property, nuint index);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial long obs_property_list_item_int(nint property, nuint index);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial double obs_property_list_item_float(nint property, nuint index);

    // ---- obs.h: encoder binding and behaviour ----

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_encoder_set_video(nint encoder, nint video);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_encoder_set_audio(nint encoder, nint audio);

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_encoder_active(nint encoder);

    // Borrowed; null until the plugin sets one.
    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_encoder_get_last_error(nint encoder);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_encoder_set_scaled_size(nint encoder, uint width, uint height);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_encoder_set_gpu_scale_type(nint encoder, int scaleType);

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_encoder_set_frame_rate_divisor(nint encoder, uint divisor);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_encoder_set_preferred_video_format(nint encoder, int format);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_encoder_set_preferred_color_space(nint encoder, int colorspace);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_encoder_set_preferred_range(nint encoder, int range);

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_encoder_scaling_enabled(nint encoder);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial uint obs_encoder_get_width(nint encoder);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial uint obs_encoder_get_height(nint encoder);

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_encoder_gpu_scaling_enabled(nint encoder);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial int obs_encoder_get_scale_type(nint encoder);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial uint obs_encoder_get_frame_rate_divisor(nint encoder);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial uint obs_encoder_get_encoded_frames(nint encoder);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial uint obs_encoder_get_sample_rate(nint encoder);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nuint obs_encoder_get_frame_size(nint encoder);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nuint obs_encoder_get_mixer_index(nint encoder);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial int obs_encoder_get_preferred_video_format(nint encoder);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial int obs_encoder_get_preferred_color_space(nint encoder);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial int obs_encoder_get_preferred_range(nint encoder);

    // Region of interest. Absent encoders answer false; the capability flag is OBS_ENCODER_CAP_ROI.
    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_encoder_add_roi(nint encoder, in ObsEncoderRoiNative roi);

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_encoder_has_roi(nint encoder);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_encoder_clear_roi(nint encoder);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial uint obs_encoder_get_roi_increment(nint encoder);

    // ---- obs.h: outputs ----
    //
    // obs_output_create hands back a non-null placeholder for an unregistered id — measured, and the
    // reason the binding probes obs_output_get_display_name first. The id and name are copied, like
    // obs_source_create. The settings object is retained, not copied.

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint obs_output_create(string id, string name, nint settings, nint hotkeyData);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_output_release(nint output);

    // Borrowed.
    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_output_get_name(nint output);

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_output_start(nint output);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_output_stop(nint output);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_output_force_stop(nint output);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_output_set_delay(nint output, uint delaySec, uint flags);

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_output_active(nint output);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial uint obs_output_get_flags(nint output);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint obs_output_defaults(string id);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_output_update(nint output, nint settings);

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_output_can_pause(nint output);

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_output_pause(nint output, [MarshalAs(UnmanagedType.U1)] bool pause);

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_output_paused(nint output);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_output_get_settings(nint output);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_output_get_signal_handler(nint output);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_output_set_media(nint output, nint video, nint audio);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_output_set_mixer(nint output, nuint mixerIndex);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nuint obs_output_get_mixer(nint output);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_output_set_mixers(nint output, nuint mixers);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nuint obs_output_get_mixers(nint output);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_output_set_video_encoder(nint output, nint encoder);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_output_set_video_encoder2(nint output, nint encoder, nuint index);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_output_set_audio_encoder(nint output, nint encoder, nuint index);

    // All three getters return the output's own pointer with no reference taken — they are a plain
    // read of output->{video,audio}_encoders[idx], in 30.0.2 and 32.2.2 alike. Releasing what they
    // hand back frees an encoder the output is still pointing at, and the damage only surfaces at
    // obs_shutdown. Take a reference with obs_encoder_get_ref before wrapping one.
    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_output_get_video_encoder(nint output);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_output_get_video_encoder2(nint output, nuint index);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_output_get_audio_encoder(nint output, nuint index);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_output_set_service(nint output, nint service);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_output_set_reconnect_settings(nint output, int retryCount, int retrySec);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial ulong obs_output_get_total_bytes(nint output);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial int obs_output_get_frames_dropped(nint output);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial int obs_output_get_total_frames(nint output);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial void obs_output_set_preferred_size(nint output, uint width, uint height);

    // Borrowed.
    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_output_get_id(nint output);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial float obs_output_get_congestion(nint output);

    [LibraryImport(ObsLibrary.Name)]
    internal static partial int obs_output_get_connect_time_ms(nint output);

    [LibraryImport(ObsLibrary.Name)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_output_reconnecting(nint output);

    // Borrowed; null until the plugin sets one.
    [LibraryImport(ObsLibrary.Name)]
    internal static partial nint obs_output_get_last_error(nint output);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint obs_get_output_properties(string id);

    // The type-level probes. obs_output_get_display_name returns NULL for an id no module registered,
    // and that is the reliable unavailability test: obs_output_create still answers a non-null
    // placeholder for an unknown id, measured, so a create result cannot stand in for this probe.
    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint obs_output_get_display_name(string id);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial uint obs_get_output_flags(string id);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool obs_enum_output_types(nuint index, out nint id);

    // ---- callback/signal.h and callback/calldata.h ----
    //
    // The signal callback is signal_callback_t: void (param, calldata). The global variant adds the
    // signal name; only the plain form is used here.

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void signal_handler_connect(
        nint handler, string signal, delegate* unmanaged[Cdecl]<nint, nint, void> callback, nint parameter);

    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void signal_handler_disconnect(
        nint handler, string signal, delegate* unmanaged[Cdecl]<nint, nint, void> callback, nint parameter);

    // Reads a calldata field by name into a buffer of the right size. calldata ints are long long, so
    // a caller reading the stop signal's "code" must pass sizeof(long) — the inline wrappers are
    // static inline and therefore not exported.
    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool calldata_get_data(nint calldata, string name, void* value, nuint size);

    // Returns a borrowed pointer to the string in the calldata.
    [LibraryImport(ObsLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool calldata_get_string(nint calldata, string name, nint* value);

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
