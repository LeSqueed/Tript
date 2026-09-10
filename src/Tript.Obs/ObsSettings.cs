// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs.Interop;

namespace Tript.Obs;

public sealed class ObsSettings : IDisposable
{
    private readonly ObsSettingsHandle _handle;

    public ObsSettings() : this(Create())
    {
    }

    private ObsSettings(nint pointer) => _handle = new ObsSettingsHandle(pointer);

    internal static ObsSettings FromOwnedPointer(nint pointer)
    {
        if (pointer == nint.Zero)
            throw new ObsException("libobs returned a null settings object where one was expected.");

        return new ObsSettings(pointer);
    }

    internal static ObsSettings? FromOwnedPointerOrNull(nint pointer) =>
        pointer == nint.Zero ? null : new ObsSettings(pointer);

    internal nint Pointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed || _handle.IsInvalid, this);
            return _handle.DangerousGetHandle();
        }
    }

    public static ObsSettings? FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return FromOwnedPointerOrNull(ObsNative.obs_data_create_from_json(json));
    }

    public static ObsSettings? FromJsonFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return FromOwnedPointerOrNull(ObsNative.obs_data_create_from_json_file(path));
    }

    public static ObsSettings? FromJsonFile(string path, string backupExtension)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(backupExtension);
        return FromOwnedPointerOrNull(ObsNative.obs_data_create_from_json_file_safe(path, backupExtension));
    }

    public void Dispose() => _handle.Dispose();

    public void SetString(string name, string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObsNative.obs_data_set_string(Pointer, name, value);
    }

    public void SetInt(string name, long value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObsNative.obs_data_set_int(Pointer, name, value);
    }

    public void SetDouble(string name, double value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObsNative.obs_data_set_double(Pointer, name, value);
    }

    public void SetBool(string name, bool value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObsNative.obs_data_set_bool(Pointer, name, value);
    }

    public void SetObject(string name, ObsSettings? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObsNative.obs_data_set_obj(Pointer, name, value?.Pointer ?? nint.Zero);
    }

    public void SetArray(string name, ObsSettingsArray? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObsNative.obs_data_set_array(Pointer, name, value?.Pointer ?? nint.Zero);
    }

    public string GetString(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return Utf8Marshal.ReadBorrowed(ObsNative.obs_data_get_string(Pointer, name)) ?? string.Empty;
    }

    public long GetInt(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return ObsNative.obs_data_get_int(Pointer, name);
    }

    public double GetDouble(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return ObsNative.obs_data_get_double(Pointer, name);
    }

    public bool GetBool(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return ObsNative.obs_data_get_bool(Pointer, name);
    }

    public ObsSettings? GetObject(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return FromOwnedPointerOrNull(ObsNative.obs_data_get_obj(Pointer, name));
    }

    public ObsSettingsArray? GetArray(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return ObsSettingsArray.FromOwnedPointerOrNull(ObsNative.obs_data_get_array(Pointer, name));
    }

    public void SetDefaultString(string name, string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObsNative.obs_data_set_default_string(Pointer, name, value);
    }

    public void SetDefaultInt(string name, long value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObsNative.obs_data_set_default_int(Pointer, name, value);
    }

    public void SetDefaultDouble(string name, double value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObsNative.obs_data_set_default_double(Pointer, name, value);
    }

    public void SetDefaultBool(string name, bool value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObsNative.obs_data_set_default_bool(Pointer, name, value);
    }

    public void SetDefaultObject(string name, ObsSettings? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObsNative.obs_data_set_default_obj(Pointer, name, value?.Pointer ?? nint.Zero);
    }

    public void SetDefaultArray(string name, ObsSettingsArray? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObsNative.obs_data_set_default_array(Pointer, name, value?.Pointer ?? nint.Zero);
    }

    public string GetDefaultString(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return Utf8Marshal.ReadBorrowed(ObsNative.obs_data_get_default_string(Pointer, name)) ?? string.Empty;
    }

    public long GetDefaultInt(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return ObsNative.obs_data_get_default_int(Pointer, name);
    }

    public double GetDefaultDouble(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return ObsNative.obs_data_get_default_double(Pointer, name);
    }

    public bool GetDefaultBool(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return ObsNative.obs_data_get_default_bool(Pointer, name);
    }

    public ObsSettings? GetDefaultObject(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return FromOwnedPointerOrNull(ObsNative.obs_data_get_default_obj(Pointer, name));
    }

    public ObsSettingsArray? GetDefaultArray(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return ObsSettingsArray.FromOwnedPointerOrNull(ObsNative.obs_data_get_default_array(Pointer, name));
    }

    public ObsSettings GetDefaults() => FromOwnedPointer(ObsNative.obs_data_get_defaults(Pointer));

    public bool HasUserValue(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return ObsNative.obs_data_has_user_value(Pointer, name);
    }

    public bool HasDefaultValue(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return ObsNative.obs_data_has_default_value(Pointer, name);
    }

    public void Erase(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObsNative.obs_data_erase(Pointer, name);
    }

    public void UnsetUserValue(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObsNative.obs_data_unset_user_value(Pointer, name);
    }

    public void UnsetDefaultValue(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObsNative.obs_data_unset_default_value(Pointer, name);
    }

    public void Clear() => ObsNative.obs_data_clear(Pointer);

    public void Apply(ObsSettings other)
    {
        ArgumentNullException.ThrowIfNull(other);
        ObsNative.obs_data_apply(Pointer, other.Pointer);
    }

    public string ToJson(bool pretty = false, bool includeDefaults = false)
    {
        var pointer = (pretty, includeDefaults) switch
        {
            (false, false) => ObsNative.obs_data_get_json(Pointer),
            (false, true) => ObsNative.obs_data_get_json_with_defaults(Pointer),
            (true, false) => ObsNative.obs_data_get_json_pretty(Pointer),
            (true, true) => ObsNative.obs_data_get_json_pretty_with_defaults(Pointer)
        };

        return Utf8Marshal.ReadBorrowed(pointer) ?? string.Empty;
    }

    public bool SaveJson(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return ObsNative.obs_data_save_json(Pointer, path);
    }

    public bool SaveJson(string path, string tempExtension, string backupExtension, bool pretty = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(tempExtension);
        ArgumentNullException.ThrowIfNull(backupExtension);

        return pretty
            ? ObsNative.obs_data_save_json_pretty_safe(Pointer, path, tempExtension, backupExtension)
            : ObsNative.obs_data_save_json_safe(Pointer, path, tempExtension, backupExtension);
    }

    public IReadOnlyList<ObsSettingsEntry> EnumerateEntries()
    {
        var entries = new List<ObsSettingsEntry>();
        var item = ObsNative.obs_data_first(Pointer);

        try
        {
            while (item != nint.Zero)
            {
                var name = Utf8Marshal.ReadBorrowed(ObsNative.obs_data_item_get_name(item));
                if (name is not null)
                    entries.Add(new ObsSettingsEntry(
                        name,
                        (ObsSettingsValueType)ObsNative.obs_data_item_gettype(item),
                        (ObsSettingsNumberType)ObsNative.obs_data_item_numtype(item),
                        ObsNative.obs_data_item_has_user_value(item),
                        ObsNative.obs_data_item_has_default_value(item)));

                if (!ObsNative.obs_data_item_next(ref item))
                    item = nint.Zero;
            }
        }
        finally
        {
            if (item != nint.Zero)
                ObsNative.obs_data_item_release(ref item);
        }

        return entries;
    }

    private static nint Create()
    {
        var pointer = ObsNative.obs_data_create();
        if (pointer == nint.Zero)
            throw new ObsException("obs_data_create returned null. It reports no reason; the log handler is where one would appear.");

        return pointer;
    }
}

public readonly record struct ObsSettingsEntry(
    string Name,
    ObsSettingsValueType ValueType,
    ObsSettingsNumberType NumberType,
    bool HasUserValue,
    bool HasDefaultValue);
