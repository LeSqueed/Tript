// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Numerics;
using Tript.Obs.Interop;

namespace Tript.Obs;

public sealed class ObsSceneItem : IDisposable
{
    private readonly ObsSceneItemHandle _handle;

    private ObsSceneItem(nint pointer) => _handle = new ObsSceneItemHandle(pointer);

    internal static ObsSceneItem FromBorrowedPointer(nint pointer)
    {
        if (pointer == nint.Zero)
            throw new ObsException("libobs returned a null scene item where one was expected.");

        ObsNative.obs_sceneitem_addref(pointer);
        return new ObsSceneItem(pointer);
    }

    internal static ObsSceneItem? FromBorrowedPointerOrNull(nint pointer) =>
        pointer == nint.Zero ? null : FromBorrowedPointer(pointer);

    // For a pointer whose reference the caller already took, so it must not be taken twice.
    internal static ObsSceneItem FromReferencedPointer(nint pointer)
    {
        if (pointer == nint.Zero)
            throw new ObsException("libobs returned a null scene item where one was expected.");

        return new ObsSceneItem(pointer);
    }

    internal nint Pointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed || _handle.IsInvalid, this);
            return _handle.DangerousGetHandle();
        }
    }

    public void Dispose() => _handle.Dispose();

    public long Id => ObsNative.obs_sceneitem_get_id(Pointer);

    public ObsSource? GetSource()
    {
        var source = ObsNative.obs_sceneitem_get_source(Pointer);
        return source == nint.Zero ? null : ObsSource.FromOwnedPointer(ObsNative.obs_source_get_ref(source));
    }

    public bool IsAttached => ObsNative.obs_sceneitem_get_scene(Pointer) != nint.Zero;

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

    public float Rotation
    {
        get => ObsNative.obs_sceneitem_get_rot(Pointer);
        set => ObsNative.obs_sceneitem_set_rot(Pointer, value);
    }

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

    public ObsAlignment Alignment
    {
        get => (ObsAlignment)ObsNative.obs_sceneitem_get_alignment(Pointer);
        set => ObsNative.obs_sceneitem_set_alignment(Pointer, (uint)value);
    }

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

    public bool CropToBounds
    {
        get => ObsNative.obs_sceneitem_get_bounds_crop(Pointer);
        set => ObsNative.obs_sceneitem_set_bounds_crop(Pointer, value);
    }

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

    public int OrderPosition
    {
        get => ObsNative.obs_sceneitem_get_order_position(Pointer);
        set => ObsNative.obs_sceneitem_set_order_position(Pointer, value);
    }

    public void MoveInOrder(ObsOrderMovement movement) => ObsNative.obs_sceneitem_set_order(Pointer, (int)movement);

    public bool SetVisible(bool visible) => ObsNative.obs_sceneitem_set_visible(Pointer, visible);

    public bool IsVisible => ObsNative.obs_sceneitem_visible(Pointer);

    public bool SetLocked(bool locked) => ObsNative.obs_sceneitem_set_locked(Pointer, locked);

    public bool IsLocked => ObsNative.obs_sceneitem_locked(Pointer);

    public void Select(bool selected) => ObsNative.obs_sceneitem_select(Pointer, selected);

    public bool IsSelected => ObsNative.obs_sceneitem_selected(Pointer);

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

    public void Remove() => ObsNative.obs_sceneitem_remove(Pointer);

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
