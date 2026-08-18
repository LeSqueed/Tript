// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs.Interop;

namespace Tript.Obs;

// libobs's obs_encoder_t: an audio or video codec context, configured through the same obs_data
// settings every source uses. A recorder owns a video encoder and an audio encoder and hands them
// to an output in Layer 5; everything here is the configuration that must be right before that.
//
// Four measured properties of the native object shape this class, none of them stated in the
// headers:
//
//   * Creation copies the id and the name. Nothing has to be kept alive, like obs_source_create
//     and unlike obs_reset_video.
//   * The settings object is *shared*, not copied: obs_encoder_get_settings hands back the very
//     obs_data_t the encoder was created with, and an edit through the caller's own reference is
//     visible to the encoder. See CreateVideo.
//   * Both create functions answer an *unregistered* id with a non-null placeholder — measured —
//     so the binding rejects the id up front, like ObsSource, rather than passing it through.
//   * obs_shutdown destroys every encoder regardless of outstanding references, so a handle that
//     outlives the context has nothing to release — hence ObsEncoderHandle being context-owned.
//
// The width, height and sample-rate getters only mean something once the encoder is bound to a
// video or audio mix; until then they report zero, which is libobs's answer rather than an error.
//
// Not thread-safe as a wrapper. libobs guards the encoder's own state; a read-modify-write through
// this class is not atomic.
public sealed class ObsEncoder : IDisposable
{
    private readonly ObsEncoderHandle _handle;

    private ObsEncoder(nint pointer) => _handle = new ObsEncoderHandle(pointer);

    internal static ObsEncoder FromOwnedPointer(nint pointer)
    {
        if (pointer == nint.Zero)
            throw new ObsException("libobs returned a null encoder where one was expected.");

        return new ObsEncoder(pointer);
    }

    internal static ObsEncoder? FromOwnedPointerOrNull(nint pointer) =>
        pointer == nint.Zero ? null : new ObsEncoder(pointer);

    internal nint Pointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed || _handle.IsInvalid, this);
            return _handle.DangerousGetHandle();
        }
    }

    // ---- creation ----

    // Creates an encoder of a registered type. The settings object, if given, is *retained* rather
    // than copied: the encoder and the caller end up sharing it, and disposing the caller's
    // reference afterwards is safe only because libobs takes one of its own.
    public static ObsEncoder CreateVideo(string id, string name, ObsSettings? settings = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ThrowIfUnregistered(id);

        var pointer = ObsNative.obs_video_encoder_create(id, name, settings?.Pointer ?? nint.Zero, nint.Zero);
        if (pointer == nint.Zero)
            throw new ObsException(
                $"obs_video_encoder_create returned null for '{id}'. It reports no reason; the log handler is where one would appear.");

        return new ObsEncoder(pointer);
    }

    // As CreateVideo, for an audio codec. The mixer index is which of the OBS audio mixers this
    // encoder draws from; zero is the programme mixer.
    public static ObsEncoder CreateAudio(string id, string name, ObsSettings? settings = null, nuint mixerIndex = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ThrowIfUnregistered(id);

        var pointer = ObsNative.obs_audio_encoder_create(id, name, settings?.Pointer ?? nint.Zero, mixerIndex, nint.Zero);
        if (pointer == nint.Zero)
            throw new ObsException(
                $"obs_audio_encoder_create returned null for '{id}'. It reports no reason; the log handler is where one would appear.");

        return new ObsEncoder(pointer);
    }

    public void Dispose() => _handle.Dispose();

    // ---- availability ----

    // Whether any loaded module registers the type. There is no obs_encoder_is_available; a plugin
    // whose hardware is absent does not register its ids, so this — or the equivalent codec probe
    // below — is the whole vocabulary availability has.
    public static bool IsTypeRegistered(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return ObsNative.obs_get_encoder_codec(id) != nint.Zero;
    }

    // The codec a type produces, e.g. "h264" or "aac". Null for an id no module registered.
    public static string? GetTypeCodec(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return Utf8Marshal.ReadBorrowed(ObsNative.obs_get_encoder_codec(id));
    }

    // What a type claims to be. Not a substitute for IsTypeRegistered: an unknown id answers Audio
    // (0) — measured — so this getter is only trustworthy once IsTypeRegistered is true.
    public static ObsEncoderType GetType(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return (ObsEncoderType)ObsNative.obs_get_encoder_type(id);
    }

    // The capability flags a type declares, read without constructing an instance. OBS_ENCODER_CAP_ROI
    // is where the ROI family is gated on.
    public static ObsEncoderCaps GetTypeCaps(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return (ObsEncoderCaps)ObsNative.obs_get_encoder_caps(id);
    }

    // The ids every loaded module registered, in registration order.
    public static IReadOnlyList<string> EnumerateTypeIds()
    {
        var ids = new List<string>();
        for (nuint index = 0; ObsNative.obs_enum_encoder_types(index, out var id); index++)
        {
            var value = Utf8Marshal.ReadBorrowed(id);
            if (value is not null)
                ids.Add(value);
        }

        return ids;
    }

    // The settings object a plugin falls back on for a type, so a caller builds its own settings by
    // starting here. Null when the id is not registered.
    public static ObsSettings? GetTypeDefaults(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return ObsSettings.FromOwnedPointerOrNull(ObsNative.obs_encoder_defaults(id));
    }

    // The properties a type exposes, free of the settings layer's round-trip assumptions: the keys
    // a plugin reads are the keys it declares here, so this is the authoritative account of what
    // can be configured and with which choices. Empty when the type is registered but declares no
    // properties.
    public static IReadOnlyList<ObsEncoderProperty> EnumerateTypeProperties(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return EnumerateProperties(ObsNative.obs_get_encoder_properties(id));
    }

    // ---- identity ----

    // The registered type id the encoder was created with.
    public string Id => Utf8Marshal.ReadBorrowed(ObsNative.obs_encoder_get_id(Pointer)) ?? string.Empty;

    public string Name
    {
        get => Utf8Marshal.ReadBorrowed(ObsNative.obs_encoder_get_name(Pointer)) ?? string.Empty;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            ObsNative.obs_encoder_set_name(Pointer, value);
        }
    }

    public ObsEncoderType Type => (ObsEncoderType)ObsNative.obs_encoder_get_type(Pointer);

    public string Codec => Utf8Marshal.ReadBorrowed(ObsNative.obs_encoder_get_codec(Pointer)) ?? string.Empty;

    public ObsEncoderCaps Caps => (ObsEncoderCaps)ObsNative.obs_encoder_get_caps(Pointer);

    // ---- settings ----

    // The encoder's live settings object, with its reference incremented — the caller disposes it.
    // Editing it does not by itself reconfigure the encoder; Update is what tells the plugin to
    // read its settings again.
    public ObsSettings GetSettings() => ObsSettings.FromOwnedPointer(ObsNative.obs_encoder_get_settings(Pointer));

    // Applies the given keys over the encoder's existing settings. The plugin is only expected to
    // honour the ones it supports changing while running — in practice bitrate, and only when
    // OBS_ENCODER_CAP_DYN_BITRATE is set.
    public void Update(ObsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObsNative.obs_encoder_update(Pointer, settings.Pointer);
    }

    // ---- binding ----

    // Binds the encoder to the running video mix, which is what makes Width and Height meaningful
    // and what scaled size and the frame-rate divisor are applied to. Requires the runtime to have
    // reset video.
    public void BindToVideo(nint video) => ObsNative.obs_encoder_set_video(Pointer, video);

    public void BindToAudio(nint audio) => ObsNative.obs_encoder_set_audio(Pointer, audio);

    // ---- state ----

    // True while the encoder is attached to an output that is running.
    public bool IsActive => ObsNative.obs_encoder_active(Pointer);

    // The plugin's own failure report, null until one is set. This is where a hardware encoder says
    // why it refused to start.
    public string? LastError => Utf8Marshal.ReadBorrowed(ObsNative.obs_encoder_get_last_error(Pointer));

    // ---- video geometry ----

    // The encoded dimensions. Zero until the encoder is bound to a video mix.
    public uint Width => ObsNative.obs_encoder_get_width(Pointer);

    public uint Height => ObsNative.obs_encoder_get_height(Pointer);

    // Pre-encode scaling, which libobs applies by copying. Disabled by default; 0,0 disables.
    public void SetScaledSize(uint width, uint height) =>
        ObsNative.obs_encoder_set_scaled_size(Pointer, width, height);

    public bool ScalingEnabled => ObsNative.obs_encoder_scaling_enabled(Pointer);

    // GPU-based scaling, the alternative to the copy path above. OBS_SCALE_DISABLE is the default
    // and any other value enables; the two are not independent switches.
    public void SetGpuScaleType(ObsScaleType scaleType) =>
        ObsNative.obs_encoder_set_gpu_scale_type(Pointer, (int)scaleType);

    public bool GpuScalingEnabled => ObsNative.obs_encoder_gpu_scaling_enabled(Pointer);

    public ObsScaleType GpuScaleType => (ObsScaleType)ObsNative.obs_encoder_get_scale_type(Pointer);

    // Records at a fraction of the base frame rate: a divisor of 2 at 60 fps records at 30. The
    // header allows it on stopped encoders only, and the setter reports whether the value was
    // accepted. Both the setter and the getter round-trip before and after binding — measured.
    public bool SetFrameRateDivisor(uint divisor) =>
        ObsNative.obs_encoder_set_frame_rate_divisor(Pointer, divisor);

    // The divisor in effect, 1 when none was set.
    public uint FrameRateDivisor => ObsNative.obs_encoder_get_frame_rate_divisor(Pointer);

    // How many frames the encoder has produced. Zero until an output is actually encoding.
    public uint EncodedFrames => ObsNative.obs_encoder_get_encoded_frames(Pointer);

    // The pixel format the encoder prefers, telling libobs to convert to it if the mix differs.
    // None is "convert only if necessary", which is the default.
    public ObsVideoFormat PreferredVideoFormat
    {
        get => (ObsVideoFormat)ObsNative.obs_encoder_get_preferred_video_format(Pointer);
        set => ObsNative.obs_encoder_set_preferred_video_format(Pointer, (int)value);
    }

    // The colour space and range pair for simultaneous SDR and HDR output. The header says these
    // are only supported when GPU scaling is enabled; the getters still read back what was set
    // before any scaling exists — measured — so a readback here is not evidence the encoder will
    // honour it.
    public ObsColorSpace PreferredColorSpace
    {
        get => (ObsColorSpace)ObsNative.obs_encoder_get_preferred_color_space(Pointer);
        set => ObsNative.obs_encoder_set_preferred_color_space(Pointer, (int)value);
    }

    public ObsVideoRange PreferredRange
    {
        get => (ObsVideoRange)ObsNative.obs_encoder_get_preferred_range(Pointer);
        set => ObsNative.obs_encoder_set_preferred_range(Pointer, (int)value);
    }

    // ---- audio ----

    // The sample rate the encoder will produce. Zero until the encoder is bound to an audio mix.
    public uint SampleRate => ObsNative.obs_encoder_get_sample_rate(Pointer);

    // The frame size an audio packet carries. Zero until the plugin reports one — ffmpeg_aac never
    // does before encoding starts, measured.
    public nuint FrameSize => ObsNative.obs_encoder_get_frame_size(Pointer);

    // Which audio mixer the encoder draws from; the value passed at creation.
    public nuint MixerIndex => ObsNative.obs_encoder_get_mixer_index(Pointer);

    // ---- region of interest ----

    // Asks the encoder to prioritise the given rectangle; returns false when the encoder does not
    // support ROI or the region is invalid. Gated on OBS_ENCODER_CAP_ROI.
    public bool AddRoi(ObsEncoderRoi roi)
    {
        var native = new ObsEncoderRoiNative
        {
            Top = roi.Top,
            Bottom = roi.Bottom,
            Left = roi.Left,
            Right = roi.Right,
            Priority = roi.Priority
        };

        return ObsNative.obs_encoder_add_roi(Pointer, native);
    }

    public bool HasRoi => ObsNative.obs_encoder_has_roi(Pointer);

    public void ClearRoi() => ObsNative.obs_encoder_clear_roi(Pointer);

    public uint RoiIncrement => ObsNative.obs_encoder_get_roi_increment(Pointer);

    private static void ThrowIfUnregistered(string id)
    {
        if (ObsNative.obs_get_encoder_codec(id) == nint.Zero)
            throw new ObsException(
                $"No loaded module registers an encoder type with the id '{id}'. " +
                "libobs would answer this with a placeholder encoder that encodes nothing.");
    }

    private static IReadOnlyList<ObsEncoderProperty> EnumerateProperties(nint propsPointer)
    {
        if (propsPointer == nint.Zero)
            return [];

        try
        {
            var properties = new List<ObsEncoderProperty>();
            var property = ObsNative.obs_properties_first(propsPointer);

            while (property != nint.Zero)
            {
                var name = Utf8Marshal.ReadBorrowed(ObsNative.obs_property_name(property)) ?? string.Empty;
                var type = (ObsPropertyType)ObsNative.obs_property_get_type(property);
                var items = ReadListItems(property, type);

                properties.Add(new ObsEncoderProperty(name, type, items));

                // Releases the current item and overwrites it with the next, so there is exactly one
                // live item at a time and nothing to release once it returns false.
                if (!ObsNative.obs_property_next(ref property))
                    property = nint.Zero;
            }

            return properties;
        }
        finally
        {
            ObsNative.obs_properties_destroy(propsPointer);
        }
    }

    // The items of a List property, each carrying the value in the format the list declares. An
    // item read with the wrong accessor returns 0 or null silently, so the format is what decides
    // which accessor is used. Non-list properties have no items.
    private static IReadOnlyList<ObsEncoderPropertyItem> ReadListItems(nint property, ObsPropertyType type)
    {
        if (type is not (ObsPropertyType.List or ObsPropertyType.EditableList))
            return [];

        var format = (ObsComboFormat)ObsNative.obs_property_list_format(property);
        var count = ObsNative.obs_property_list_item_count(property);
        var items = new List<ObsEncoderPropertyItem>((int)count);

        for (nuint i = 0; i < count; i++)
        {
            object? value = format switch
            {
                ObsComboFormat.String => Utf8Marshal.ReadBorrowed(ObsNative.obs_property_list_item_string(property, i)),
                ObsComboFormat.Int => ObsNative.obs_property_list_item_int(property, i),
                ObsComboFormat.Float => ObsNative.obs_property_list_item_float(property, i),
                ObsComboFormat.Bool => ObsNative.obs_property_list_item_int(property, i) != 0,
                _ => null
            };

            items.Add(new ObsEncoderPropertyItem(value, format));
        }

        return items;
    }
}

// One property as enumeration sees it. The items are populated only for List and EditableList
// properties, which is what carries the accepted value strings a plugin compares byte for byte.
public readonly record struct ObsEncoderProperty(string Name, ObsPropertyType Type, IReadOnlyList<ObsEncoderPropertyItem> Items);

// One accepted value in a List property. Format says how to interpret Value; the two are kept
// together because the format is precisely what a wrong read discards silently.
public readonly record struct ObsEncoderPropertyItem(object? Value, ObsComboFormat Format);
