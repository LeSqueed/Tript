// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs.Interop;

namespace Tript.Obs;

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

    internal static ObsEncoder? FromBorrowedPointerOrNull(nint pointer)
    {
        if (pointer == nint.Zero)
            return null;

        var referenced = ObsNative.obs_encoder_get_ref(pointer);
        return referenced == nint.Zero ? null : new ObsEncoder(referenced);
    }

    internal nint Pointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed || _handle.IsInvalid, this);
            return _handle.DangerousGetHandle();
        }
    }

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

    public static bool IsTypeRegistered(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return ObsNative.obs_get_encoder_codec(id) != nint.Zero;
    }

    public static string? GetTypeCodec(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return Utf8Marshal.ReadBorrowed(ObsNative.obs_get_encoder_codec(id));
    }

    public static ObsEncoderType GetType(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return (ObsEncoderType)ObsNative.obs_get_encoder_type(id);
    }

    public static ObsEncoderCaps GetTypeCaps(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return (ObsEncoderCaps)ObsNative.obs_get_encoder_caps(id);
    }

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

    public static ObsSettings? GetTypeDefaults(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return ObsSettings.FromOwnedPointerOrNull(ObsNative.obs_encoder_defaults(id));
    }

    public static IReadOnlyList<ObsEncoderProperty> EnumerateTypeProperties(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return EnumerateProperties(ObsNative.obs_get_encoder_properties(id));
    }

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

    public ObsSettings GetSettings() => ObsSettings.FromOwnedPointer(ObsNative.obs_encoder_get_settings(Pointer));

    public void Update(ObsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObsNative.obs_encoder_update(Pointer, settings.Pointer);
    }

    public void BindToVideo(nint video) => ObsNative.obs_encoder_set_video(Pointer, video);

    public void BindToAudio(nint audio) => ObsNative.obs_encoder_set_audio(Pointer, audio);

    public bool IsActive => ObsNative.obs_encoder_active(Pointer);

    public string? LastError => Utf8Marshal.ReadBorrowed(ObsNative.obs_encoder_get_last_error(Pointer));

    public uint Width => ObsNative.obs_encoder_get_width(Pointer);

    public uint Height => ObsNative.obs_encoder_get_height(Pointer);

    public void SetScaledSize(uint width, uint height) =>
        ObsNative.obs_encoder_set_scaled_size(Pointer, width, height);

    public bool ScalingEnabled => ObsNative.obs_encoder_scaling_enabled(Pointer);

    public void SetGpuScaleType(ObsScaleType scaleType) =>
        ObsNative.obs_encoder_set_gpu_scale_type(Pointer, (int)scaleType);

    public bool GpuScalingEnabled => ObsNative.obs_encoder_gpu_scaling_enabled(Pointer);

    public ObsScaleType GpuScaleType => (ObsScaleType)ObsNative.obs_encoder_get_scale_type(Pointer);

    public bool SetFrameRateDivisor(uint divisor) =>
        ObsNative.obs_encoder_set_frame_rate_divisor(Pointer, divisor);

    public uint FrameRateDivisor => ObsNative.obs_encoder_get_frame_rate_divisor(Pointer);

    public uint EncodedFrames => ObsNative.obs_encoder_get_encoded_frames(Pointer);

    public ObsVideoFormat PreferredVideoFormat
    {
        get => (ObsVideoFormat)ObsNative.obs_encoder_get_preferred_video_format(Pointer);
        set => ObsNative.obs_encoder_set_preferred_video_format(Pointer, (int)value);
    }

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

    public uint SampleRate => ObsNative.obs_encoder_get_sample_rate(Pointer);

    public nuint FrameSize => ObsNative.obs_encoder_get_frame_size(Pointer);

    public nuint MixerIndex => ObsNative.obs_encoder_get_mixer_index(Pointer);

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

public readonly record struct ObsEncoderProperty(string Name, ObsPropertyType Type, IReadOnlyList<ObsEncoderPropertyItem> Items);

public readonly record struct ObsEncoderPropertyItem(object? Value, ObsComboFormat Format);
