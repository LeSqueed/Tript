// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs.Interop;

namespace Tript.Obs;

// libobs's obs_data_array_t: an ordered list of settings objects, and the only container type a
// settings bag can hold besides a nested object. Its ownership rules match ObsSettings —
// refcounted, independent of the OBS context, and holding references to its elements rather than
// copies.
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

    // The caller owns the returned object and disposes it. libobs answers an out-of-range index with
    // null rather than a fault; the range is checked here so the mistake is reported where it was
    // made instead of surfacing as a null two calls later.
    public ObsSettings this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            return ObsSettings.FromOwnedPointer(ObsNative.obs_data_array_item(Pointer, (nuint)index));
        }
    }

    // Returns the index the item landed at. The array takes its own reference, so the caller keeps
    // ownership of what it passed in and may dispose it immediately.
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

    // Appends references to the other array's elements, not copies of them.
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
