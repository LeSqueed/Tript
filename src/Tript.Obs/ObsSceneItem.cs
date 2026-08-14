// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Numerics;
using Tript.Obs.Interop;

namespace Tript.Obs;

// libobs's obs_sceneitem_t: one source's placement inside one scene. The source can appear in
// several scenes, and twice in the same scene; each appearance is a separate item with its own
// transform and its own id.
//
// Ownership is the measured part, and it is the trap in this whole surface. Every call that hands
// out a scene item — obs_scene_add, obs_scene_find_source, the enumerator — returns a *borrowed*
// pointer. The one reference the item is created with belongs to the scene, and releasing that as if
// it were the caller's frees the item while the scene still lists it: measurement shows the memory
// returned to the allocator with the scene's list untouched, which is a dangling pointer rather than
// an error anyone reports. So every handle here takes a reference of its own first, and Dispose
// balances exactly that one.
//
// Geometry note, likewise measured and stated in no header: position and bounds are snapped to a
// half-unit grid. Setting 100.125 reads back 100.0 and setting 0.75 reads back 1.0, at any canvas
// size and any scale. Rotation, scale and crop keep what they are given.
public sealed class ObsSceneItem : IDisposable
{
    private readonly ObsSceneItemHandle _handle;

    private ObsSceneItem(nint pointer) => _handle = new ObsSceneItemHandle(pointer);

    // Takes the reference this object owns. The pointer libobs handed over stays the scene's.
    internal static ObsSceneItem FromBorrowedPointer(nint pointer)
    {
        if (pointer == nint.Zero)
            throw new ObsException("libobs returned a null scene item where one was expected.");

        ObsNative.obs_sceneitem_addref(pointer);
        return new ObsSceneItem(pointer);
    }

    internal static ObsSceneItem? FromBorrowedPointerOrNull(nint pointer) =>
        pointer == nint.Zero ? null : FromBorrowedPointer(pointer);

    internal nint Pointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed || _handle.IsInvalid, this);
            return _handle.DangerousGetHandle();
        }
    }

    public void Dispose() => _handle.Dispose();

    // ---- identity ----

    // Unique within its scene and stable across reordering. Numbering starts at one.
    public long Id => ObsNative.obs_sceneitem_get_id(Pointer);

    // An owning reference to the source this item places; the caller disposes it. Still answers
    // after the item has been detached from its scene: the item's hold on its source is given up
    // when the item itself is released, not when it leaves the scene.
    public ObsSource? GetSource()
    {
        var source = ObsNative.obs_sceneitem_get_source(Pointer);
        return source == nint.Zero ? null : ObsSource.FromOwnedPointer(ObsNative.obs_source_get_ref(source));
    }

    // True while the item is still part of a scene. Removal leaves the item itself valid — this
    // handle holds it — but detached, with no scene and no source.
    public bool IsAttached => ObsNative.obs_sceneitem_get_scene(Pointer) != nint.Zero;

    // ---- transform ----

    // The whole placement in one call. Preferred over the individual properties when more than one
    // is changing: each individual setter recalculates the item's matrices on its own.
    public ObsTransform Transform
    {
        get
        {
            ObsNative.obs_sceneitem_get_info2(Pointer, out var info);
            return new ObsTransform
            {
                Position = new Vector2(info.Position.X, info.Position.Y),
                Rotation = info.Rotation,
                Scale = new Vector2(info.Scale.X, info.Scale.Y),
                Alignment = (ObsAlignment)info.Alignment,
                BoundsType = (ObsBoundsType)info.BoundsType,
                BoundsAlignment = (ObsAlignment)info.BoundsAlignment,
                Bounds = new Vector2(info.Bounds.X, info.Bounds.Y),
                CropToBounds = info.CropToBounds != 0
            };
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            var info = new ObsTransformInfoNative
            {
                Position = new Vec2Native { X = value.Position.X, Y = value.Position.Y },
                Rotation = value.Rotation,
                Scale = new Vec2Native { X = value.Scale.X, Y = value.Scale.Y },
                Alignment = (uint)value.Alignment,
                BoundsType = (int)value.BoundsType,
                BoundsAlignment = (uint)value.BoundsAlignment,
                Bounds = new Vec2Native { X = value.Bounds.X, Y = value.Bounds.Y },
                CropToBounds = value.CropToBounds ? (byte)1 : (byte)0
            };

            ObsNative.obs_sceneitem_set_info2(Pointer, ref info);
        }
    }

    // In canvas pixels, from the scene's origin to the item's alignment point. Snapped to the
    // nearest half-pixel on the way in.
    public Vector2 Position
    {
        get
        {
            ObsNative.obs_sceneitem_get_pos(Pointer, out var position);
            return new Vector2(position.X, position.Y);
        }
        set
        {
            var position = new Vec2Native { X = value.X, Y = value.Y };
            ObsNative.obs_sceneitem_set_pos(Pointer, ref position);
        }
    }

    // Degrees, clockwise. Kept exactly as given, including values past a full turn and negative ones.
    public float Rotation
    {
        get => ObsNative.obs_sceneitem_get_rot(Pointer);
        set => ObsNative.obs_sceneitem_set_rot(Pointer, value);
    }

    // A multiplier per axis, not a size. A negative component mirrors the item on that axis.
    public Vector2 Scale
    {
        get
        {
            ObsNative.obs_sceneitem_get_scale(Pointer, out var scale);
            return new Vector2(scale.X, scale.Y);
        }
        set
        {
            var scale = new Vec2Native { X = value.X, Y = value.Y };
            ObsNative.obs_sceneitem_set_scale(Pointer, ref scale);
        }
    }

    // Which point of the item Position refers to. libobs validates nothing here: bits outside the
    // four alignment flags are stored and read back unchanged.
    public ObsAlignment Alignment
    {
        get => (ObsAlignment)ObsNative.obs_sceneitem_get_alignment(Pointer);
        set => ObsNative.obs_sceneitem_set_alignment(Pointer, (uint)value);
    }

    // ---- bounds ----

    // How the item is fitted into Bounds. None ignores the bounding box entirely. Unvalidated in the
    // same way as Alignment: an undefined value round-trips rather than being rejected.
    public ObsBoundsType BoundsType
    {
        get => (ObsBoundsType)ObsNative.obs_sceneitem_get_bounds_type(Pointer);
        set => ObsNative.obs_sceneitem_set_bounds_type(Pointer, (int)value);
    }

    public ObsAlignment BoundsAlignment
    {
        get => (ObsAlignment)ObsNative.obs_sceneitem_get_bounds_alignment(Pointer);
        set => ObsNative.obs_sceneitem_set_bounds_alignment(Pointer, (uint)value);
    }

    // The bounding box in canvas pixels. Snapped to the half-pixel grid like Position.
    public Vector2 Bounds
    {
        get
        {
            ObsNative.obs_sceneitem_get_bounds(Pointer, out var bounds);
            return new Vector2(bounds.X, bounds.Y);
        }
        set
        {
            var bounds = new Vec2Native { X = value.X, Y = value.Y };
            ObsNative.obs_sceneitem_set_bounds(Pointer, ref bounds);
        }
    }

    // Crops what falls outside the bounding box rather than scaling it in.
    public bool CropToBounds
    {
        get => ObsNative.obs_sceneitem_get_bounds_crop(Pointer);
        set => ObsNative.obs_sceneitem_set_bounds_crop(Pointer, value);
    }

    // Pixels taken off each edge of the source before placement. Negative values are stored as zero.
    public ObsCrop Crop
    {
        get
        {
            ObsNative.obs_sceneitem_get_crop(Pointer, out var crop);
            return new ObsCrop(crop.Left, crop.Top, crop.Right, crop.Bottom);
        }
        set
        {
            var crop = new ObsSceneItemCropNative
            {
                Left = value.Left, Top = value.Top, Right = value.Right, Bottom = value.Bottom
            };

            ObsNative.obs_sceneitem_set_crop(Pointer, ref crop);
        }
    }

    // ---- ordering ----

    // Zero is the bottom of the scene, drawn first and therefore behind everything else. A source
    // added to a scene lands on top, with the highest position.
    public int OrderPosition
    {
        get => ObsNative.obs_sceneitem_get_order_position(Pointer);
        set => ObsNative.obs_sceneitem_set_order_position(Pointer, value);
    }

    public void MoveInOrder(ObsOrderMovement movement) => ObsNative.obs_sceneitem_set_order(Pointer, (int)movement);

    // ---- rendering ----

    // Returns true when the value changed, false when it was already what was asked for — the way
    // libobs reports "nothing to do" here, and not something the header states.
    public bool SetVisible(bool visible) => ObsNative.obs_sceneitem_set_visible(Pointer, visible);

    public bool IsVisible => ObsNative.obs_sceneitem_visible(Pointer);

    // A frontend concern that libobs stores for it: a locked item is not protected from this API.
    public bool SetLocked(bool locked) => ObsNative.obs_sceneitem_set_locked(Pointer, locked);

    public bool IsLocked => ObsNative.obs_sceneitem_locked(Pointer);

    public void Select(bool selected) => ObsNative.obs_sceneitem_select(Pointer, selected);

    public bool IsSelected => ObsNative.obs_sceneitem_selected(Pointer);

    // How the item is resampled when its scale is not 1. Disable is the default and means the
    // scene's own scaling is used.
    public ObsScaleType ScaleFilter
    {
        get => (ObsScaleType)ObsNative.obs_sceneitem_get_scale_filter(Pointer);
        set => ObsNative.obs_sceneitem_set_scale_filter(Pointer, (int)value);
    }

    public ObsBlendingMethod BlendingMethod
    {
        get => (ObsBlendingMethod)ObsNative.obs_sceneitem_get_blending_method(Pointer);
        set => ObsNative.obs_sceneitem_set_blending_method(Pointer, (int)value);
    }

    public ObsBlendingType BlendingMode
    {
        get => (ObsBlendingType)ObsNative.obs_sceneitem_get_blending_mode(Pointer);
        set => ObsNative.obs_sceneitem_set_blending_mode(Pointer, (int)value);
    }

    // ---- attachment ----

    // Detaches the item from its scene and releases the scene's reference to the item. The item's
    // own reference to its source is not given up here — that happens when the item is destroyed —
    // so a source stays alive while any handle to a detached item remains open. Calling it twice is
    // harmless.
    public void Remove() => ObsNative.obs_sceneitem_remove(Pointer);

    // Batches several transform changes into one recalculation. Reads inside the scope see the new
    // values immediately; it is the matrix work that is deferred, not the state.
    public IDisposable DeferUpdates()
    {
        ObsNative.obs_sceneitem_defer_update_begin(Pointer);
        return new DeferredUpdateScope(this);
    }

    public void ForceUpdateTransform() => ObsNative.obs_sceneitem_force_update_transform(Pointer);

    private sealed class DeferredUpdateScope : IDisposable
    {
        private readonly ObsSceneItem _item;
        private int _disposed;

        internal DeferredUpdateScope(ObsSceneItem item) => _item = item;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                ObsNative.obs_sceneitem_defer_update_end(_item.Pointer);
        }
    }
}
