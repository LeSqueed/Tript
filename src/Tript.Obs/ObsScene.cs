// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tript.Obs.Interop;

namespace Tript.Obs;

// libobs's obs_scene_t: the container that decides what the recording is made of. A scene *is* a
// source, so it can be put on an output channel like any other, and every source it contains is
// represented by a scene item that carries the placement.
//
// Two measured facts drive this class, and both are the kind that a signature cannot show:
//
//   * A scene created findable by name carries a reference held by the OBS core. Releasing the
//     caller's reference leaves it alive and still findable, so Dispose marks it removed first;
//     without that, disposing a scene leaks it for the life of the context.
//   * obs_shutdown crashes outright if a scene the caller still holds a reference to still has
//     items attached. Not a leak — a segmentation fault inside libobs. Scenes are therefore
//     registered with the runtime, which disposes any that are still live before it shuts libobs
//     down.
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

    // The scene's own source, borrowed: neither conversion between a scene and its source changes a
    // reference count, so this pointer must not be released. AsSource is the owning form.
    internal nint SourcePointer => ObsNative.obs_scene_get_source(Pointer);

    // ---- creation ----

    // A scene the rest of libobs can find by name and that a scene collection would save.
    public static ObsScene Create(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new ObsScene(CreateOrThrow(ObsNative.obs_scene_create(name), name), findableByName: true);
    }

    // A scene nothing outside this process's own references can reach. The recorder's normal case:
    // its lifetime is exactly its handle's, with no core registration to unpick.
    public static ObsScene CreatePrivate(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new ObsScene(CreateOrThrow(ObsNative.obs_scene_create_private(name), name), findableByName: false);
    }

    // The scene behind a source, or null if that source is not a scene. The reference is the
    // caller's own — obs_scene_from_source borrows, so this takes a reference before wrapping it.
    public static ObsScene? FromSource(ObsSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var scene = ObsNative.obs_scene_from_source(source.Pointer);
        if (scene == nint.Zero)
            return null;

        // Through the source, because obs_scene_get_ref and obs_source_get_ref reach the same
        // counter and only one of the two is declared here.
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

    // ---- identity ----

    public string Name
    {
        get => Utf8Marshal.ReadBorrowed(ObsNative.obs_source_get_name(SourcePointer)) ?? string.Empty;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            ObsNative.obs_source_set_name(SourcePointer, value);
        }
    }

    // An owning reference to the scene's source, for the calls that take a source rather than a
    // scene. The caller disposes it; disposing it does not dispose the scene.
    public ObsSource AsSource() => ObsSource.FromOwnedPointer(ObsNative.obs_source_get_ref(SourcePointer));

    // ---- items ----

    // Puts a source in this scene. The item takes a reference to the source, so the source outlives
    // its caller's handle for as long as the item does — measured, and the reason a source can be
    // disposed immediately after being added without the scene losing it.
    public ObsSceneItem? AddSource(ObsSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var item = ObsNative.obs_scene_add(Pointer, source.Pointer);
        return item == nint.Zero ? null : ObsSceneItem.FromBorrowedPointer(item);
    }

    // The first item whose source has this name, or null. Names are not unique; when two items carry
    // sources of the same name this finds the lower one.
    public ObsSceneItem? FindItem(string sourceName)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        return ObsSceneItem.FromBorrowedPointerOrNull(ObsNative.obs_scene_find_source(Pointer, sourceName));
    }

    // By the id libobs assigned the item when it was added, which is stable where a name is not.
    public ObsSceneItem? FindItem(long id) =>
        ObsSceneItem.FromBorrowedPointerOrNull(ObsNative.obs_scene_find_sceneitem_by_id(Pointer, id));

    // Every item in the scene, bottom first: index 0 is the item with order position 0, which is
    // drawn behind the rest. Each item in the result is a reference of its own and is disposed by
    // the caller.
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

    // Sets the z-order of every item at once, bottom first — the same order EnumerateItems returns.
    // Returns false if the array does not describe exactly this scene's items.
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

    // Runs inside libobs's scene lock, on the calling thread. It records pointers and nothing else:
    // taking a reference here would mean allocating inside the lock, and an exception crossing back
    // into libobs's frame would terminate the process.
    //
    // The gap that leaves: EnumerateItems takes its references after the enumeration has returned
    // and the lock is gone, so an item removed in between would be freed before it is wrapped.
    // Deliberately left: every caller is on the single-threaded control plane, and closing it means
    // addref-ing inside the callback and moving the wrapper to owned pointers — a change to the
    // binding's ownership rules that only a live libobs can prove. Revisit if a scene is ever
    // mutated off the control plane.
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
            // Nothing here may throw across the native frame. A collection that failed shows up as a
            // short list, which the caller sees; a process that died would not.
        }

        // Non-zero keeps the enumeration going.
        return 1;
    }

    private static nint CreateOrThrow(nint pointer, string name) =>
        pointer != nint.Zero
            ? pointer
            : throw new ObsException(
                $"obs_scene_create returned null for '{name}'. It reports no reason; the log handler is where one would appear.");
}
