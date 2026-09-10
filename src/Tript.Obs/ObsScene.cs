// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tript.Obs.Interop;

namespace Tript.Obs;

public sealed class ObsScene : IDisposable
{
    private readonly ObsSceneHandle _handle;

    private ObsScene(nint pointer, bool findableByName)
    {
        _handle = new ObsSceneHandle(pointer, findableByName);
        ObsRuntime.RegisterScene(this);
    }

    internal nint Pointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed || _handle.IsInvalid, this);
            return _handle.DangerousGetHandle();
        }
    }

    internal nint SourcePointer => ObsNative.obs_scene_get_source(Pointer);

    public static ObsScene Create(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new ObsScene(CreateOrThrow(ObsNative.obs_scene_create(name), name), findableByName: true);
    }

    public static ObsScene CreatePrivate(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new ObsScene(CreateOrThrow(ObsNative.obs_scene_create_private(name), name), findableByName: false);
    }

    public static ObsScene? FromSource(ObsSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var scene = ObsNative.obs_scene_from_source(source.Pointer);
        if (scene == nint.Zero)
            return null;

        ObsNative.obs_source_get_ref(ObsNative.obs_scene_get_source(scene));
        return new ObsScene(scene, findableByName: false);
    }

    public void Dispose()
    {
        if (_handle.IsClosed)
            return;

        ObsRuntime.UnregisterScene(this);
        _handle.Dispose();
    }

    public string Name
    {
        get => Utf8Marshal.ReadBorrowed(ObsNative.obs_source_get_name(SourcePointer)) ?? string.Empty;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            ObsNative.obs_source_set_name(SourcePointer, value);
        }
    }

    public ObsSource AsSource() => ObsSource.FromOwnedPointer(ObsNative.obs_source_get_ref(SourcePointer));

    public ObsSceneItem? AddSource(ObsSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var item = ObsNative.obs_scene_add(Pointer, source.Pointer);
        return item == nint.Zero ? null : ObsSceneItem.FromBorrowedPointer(item);
    }

    public ObsSceneItem? FindItem(string sourceName)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        return ObsSceneItem.FromBorrowedPointerOrNull(ObsNative.obs_scene_find_source(Pointer, sourceName));
    }

    public ObsSceneItem? FindItem(long id) =>
        ObsSceneItem.FromBorrowedPointerOrNull(ObsNative.obs_scene_find_sceneitem_by_id(Pointer, id));

    public IReadOnlyList<ObsSceneItem> EnumerateItems()
    {
        var pointers = new List<nint>();
        var collector = GCHandle.Alloc(pointers);

        try
        {
            unsafe
            {
                ObsNative.obs_scene_enum_items(Pointer, &Collect, GCHandle.ToIntPtr(collector));
            }
        }
        finally
        {
            collector.Free();
        }

        var items = new List<ObsSceneItem>(pointers.Count);
        foreach (var pointer in pointers)
            items.Add(ObsSceneItem.FromBorrowedPointer(pointer));

        return items;
    }

    public bool Reorder(IReadOnlyList<ObsSceneItem> bottomToTop)
    {
        ArgumentNullException.ThrowIfNull(bottomToTop);

        var pointers = new nint[bottomToTop.Count];
        for (var i = 0; i < bottomToTop.Count; i++)
        {
            ArgumentNullException.ThrowIfNull(bottomToTop[i]);
            pointers[i] = bottomToTop[i].Pointer;
        }

        unsafe
        {
            fixed (nint* first = pointers)
            {
                return ObsNative.obs_scene_reorder_items(Pointer, first, (nuint)pointers.Length);
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte Collect(nint scene, nint item, nint parameter)
    {
        try
        {
            if (GCHandle.FromIntPtr(parameter).Target is List<nint> pointers)
                pointers.Add(item);
        }
        catch
        {
        }

        return 1;
    }

    private static nint CreateOrThrow(nint pointer, string name) =>
        pointer != nint.Zero
            ? pointer
            : throw new ObsException(
                $"obs_scene_create returned null for '{name}'. It reports no reason; the log handler is where one would appear.");
}
