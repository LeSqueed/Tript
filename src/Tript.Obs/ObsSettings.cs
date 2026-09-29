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

    public void SetBool(string name, bool value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObsNative.obs_data_set_bool(Pointer, name, value);
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

    public bool GetBool(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return ObsNative.obs_data_get_bool(Pointer, name);
    }

    public bool HasUserValue(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return ObsNative.obs_data_has_user_value(Pointer, name);
    }

    public void Clear() => ObsNative.obs_data_clear(Pointer);

    public void Apply(ObsSettings other)
    {
        ArgumentNullException.ThrowIfNull(other);
        ObsNative.obs_data_apply(Pointer, other.Pointer);
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

    private static nint Create()
    {
        var pointer = ObsNative.obs_data_create();
        if (pointer == nint.Zero)
            throw new ObsException("obs_data_create returned null. It reports no reason; the log handler is where one would appear.");

        return pointer;
    }
}
