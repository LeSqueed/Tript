// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs.Interop;

namespace Tript.Obs;

public sealed class ObsSettingsArray : IDisposable
{
    private readonly ObsSettingsArrayHandle _handle;

    public ObsSettingsArray() : this(Create())
    {
    }

    private ObsSettingsArray(nint pointer) => _handle = new ObsSettingsArrayHandle(pointer);

    internal static ObsSettingsArray? FromOwnedPointerOrNull(nint pointer) =>
        pointer == nint.Zero ? null : new ObsSettingsArray(pointer);

    internal nint Pointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed || _handle.IsInvalid, this);
            return _handle.DangerousGetHandle();
        }
    }

    public void Dispose() => _handle.Dispose();

    public int Count => checked((int)ObsNative.obs_data_array_count(Pointer));

    public ObsSettings this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            return ObsSettings.FromOwnedPointer(ObsNative.obs_data_array_item(Pointer, (nuint)index));
        }
    }

    public int Add(ObsSettings item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return checked((int)ObsNative.obs_data_array_push_back(Pointer, item.Pointer));
    }

    public void Insert(int index, ObsSettings item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, Count);
        ObsNative.obs_data_array_insert(Pointer, (nuint)index, item.Pointer);
    }

    public void AddRange(ObsSettingsArray other)
    {
        ArgumentNullException.ThrowIfNull(other);
        ObsNative.obs_data_array_push_back_array(Pointer, other.Pointer);
    }

    public void RemoveAt(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
        ObsNative.obs_data_array_erase(Pointer, (nuint)index);
    }

    private static nint Create()
    {
        var pointer = ObsNative.obs_data_array_create();
        if (pointer == nint.Zero)
            throw new ObsException("obs_data_array_create returned null. It reports no reason; the log handler is where one would appear.");

        return pointer;
    }
}
