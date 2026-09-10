// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs.Interop;

namespace Tript.Obs;

public sealed class ObsSource : IDisposable
{
    private readonly ObsSourceHandle _handle;

    private ObsSource(nint pointer) => _handle = new ObsSourceHandle(pointer);

    internal static ObsSource FromOwnedPointer(nint pointer)
    {
        if (pointer == nint.Zero)
            throw new ObsException("libobs returned a null source where one was expected.");

        return new ObsSource(pointer);
    }

    internal static ObsSource? FromOwnedPointerOrNull(nint pointer) =>
        pointer == nint.Zero ? null : new ObsSource(pointer);

    internal nint Pointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed || _handle.IsInvalid, this);
            return _handle.DangerousGetHandle();
        }
    }

    public static ObsSource Create(string id, string name, ObsSettings? settings = null) =>
        Create(id, name, settings, findableByName: true);

    public static ObsSource CreatePrivate(string id, string name, ObsSettings? settings = null) =>
        Create(id, name, settings, findableByName: false);

    private static ObsSource Create(string id, string name, ObsSettings? settings, bool findableByName)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (GetTypeDisplayName(id) is null)
            throw new ObsException(
                $"No loaded module registers a source type with the id '{id}'. " +
                "libobs would answer this with a placeholder source that renders nothing.");

        var pointer = findableByName
            ? ObsNative.obs_source_create(id, name, settings?.Pointer ?? nint.Zero, nint.Zero)
            : ObsNative.obs_source_create_private(id, name, settings?.Pointer ?? nint.Zero);

        if (pointer == nint.Zero)
            throw new ObsException(
                $"obs_source_create returned null for '{id}'. It reports no reason; the log handler is where one would appear.");

        return new ObsSource(pointer);
    }

    public static string? GetTypeDisplayName(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return Utf8Marshal.ReadBorrowed(ObsNative.obs_source_get_display_name(id));
    }

    public static ObsSourceOutputFlags GetTypeOutputFlags(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return (ObsSourceOutputFlags)ObsNative.obs_get_source_output_flags(id);
    }

    public static ObsSource? FindByName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return FromOwnedPointerOrNull(ObsNative.obs_get_source_by_name(name));
    }

    public ObsSource AddReference() => FromOwnedPointer(ObsNative.obs_source_get_ref(Pointer));

    public ObsWeakSource CreateWeakReference() =>
        ObsWeakSource.FromOwnedPointer(ObsNative.obs_source_get_weak_source(Pointer));

    public void Dispose() => _handle.Dispose();

    public string Id => Utf8Marshal.ReadBorrowed(ObsNative.obs_source_get_id(Pointer)) ?? string.Empty;

    public string UnversionedId =>
        Utf8Marshal.ReadBorrowed(ObsNative.obs_source_get_unversioned_id(Pointer)) ?? string.Empty;

    public string Name
    {
        get => Utf8Marshal.ReadBorrowed(ObsNative.obs_source_get_name(Pointer)) ?? string.Empty;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            ObsNative.obs_source_set_name(Pointer, value);
        }
    }

    public string Uuid => Utf8Marshal.ReadBorrowed(ObsNative.obs_source_get_uuid(Pointer)) ?? string.Empty;

    public ObsSourceType Type => (ObsSourceType)ObsNative.obs_source_get_type(Pointer);

    public ObsSourceOutputFlags OutputFlags => (ObsSourceOutputFlags)ObsNative.obs_source_get_output_flags(Pointer);

    public bool IsScene => ObsNative.obs_source_is_scene(Pointer);

    public ObsSettings GetSettings() => ObsSettings.FromOwnedPointer(ObsNative.obs_source_get_settings(Pointer));

    public IReadOnlyList<ObsSourceProperty> EnumerateProperties() =>
        ObsSourceProperties.EnumerateProperties(ObsNative.obs_source_properties(Pointer));

    public void Update(ObsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObsNative.obs_source_update(Pointer, settings.Pointer);
    }

    public void ResetSettings(ObsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObsNative.obs_source_reset_settings(Pointer, settings.Pointer);
    }

    public uint Width => ObsNative.obs_source_get_width(Pointer);

    public uint Height => ObsNative.obs_source_get_height(Pointer);

    public ObsSourceColorSpace GetColorSpace(params ObsSourceColorSpace[] acceptable)
    {
        ArgumentNullException.ThrowIfNull(acceptable);

        if (acceptable.Length == 0)
            return (ObsSourceColorSpace)ObsNative.obs_source_get_color_space(Pointer, 0, null);

        var spaces = Array.ConvertAll(acceptable, space => (int)space);
        return (ObsSourceColorSpace)ObsNative.obs_source_get_color_space(
            Pointer, (nuint)spaces.Length, spaces);
    }

    public ObsSourceColorSpace ColorSpace => GetColorSpace(
        ObsSourceColorSpace.Srgb,
        ObsSourceColorSpace.Srgb16F,
        ObsSourceColorSpace.Extended709,
        ObsSourceColorSpace.Scrgb709);

    public uint BaseWidth => ObsNative.obs_source_get_base_width(Pointer);

    public uint BaseHeight => ObsNative.obs_source_get_base_height(Pointer);

    public bool IsEnabled
    {
        get => ObsNative.obs_source_enabled(Pointer);
        set => ObsNative.obs_source_set_enabled(Pointer, value);
    }

    public bool IsActive => ObsNative.obs_source_active(Pointer);

    public bool IsShowing => ObsNative.obs_source_showing(Pointer);

    public uint AudioMixers
    {
        get => ObsNative.obs_source_get_audio_mixers(Pointer);
        set => ObsNative.obs_source_set_audio_mixers(Pointer, value);
    }

    public float Volume
    {
        get => ObsNative.obs_source_get_volume(Pointer);
        set => ObsNative.obs_source_set_volume(Pointer, value);
    }

    public void MarkActive() => ObsNative.obs_source_inc_active(Pointer);

    public void MarkInactive() => ObsNative.obs_source_dec_active(Pointer);

    public void MarkRemoved() => ObsNative.obs_source_remove(Pointer);

    public bool IsRemoved => ObsNative.obs_source_removed(Pointer);
}

public sealed class ObsWeakSource : IDisposable
{
    private readonly ObsWeakSourceHandle _handle;

    private ObsWeakSource(nint pointer) => _handle = new ObsWeakSourceHandle(pointer);

    internal static ObsWeakSource FromOwnedPointer(nint pointer)
    {
        if (pointer == nint.Zero)
            throw new ObsException("libobs returned a null weak source where one was expected.");

        return new ObsWeakSource(pointer);
    }

    private nint Pointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed || _handle.IsInvalid, this);
            return _handle.DangerousGetHandle();
        }
    }

    public bool IsExpired => ObsNative.obs_weak_source_expired(Pointer);

    public ObsSource? TryGetSource() => ObsSource.FromOwnedPointerOrNull(ObsNative.obs_weak_source_get_source(Pointer));

    public void Dispose() => _handle.Dispose();
}
