// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Numerics;

namespace Tript.Obs;

// struct obs_transform_info: a scene item's whole placement in one value. The individual setters
// exist too, but each of them recalculates the item's matrices, so setting eight properties one at
// a time does eight times the work of setting this.
public sealed record ObsTransform
{
    public Vector2 Position { get; init; }

    // Degrees, not radians, and not wrapped: libobs stores 400.75 and -12.25 unchanged.
    public float Rotation { get; init; }

    public Vector2 Scale { get; init; } = Vector2.One;

    public ObsAlignment Alignment { get; init; } = ObsAlignment.Left | ObsAlignment.Top;

    public ObsBoundsType BoundsType { get; init; }

    public ObsAlignment BoundsAlignment { get; init; }

    // Only consulted when BoundsType is anything other than None.
    public Vector2 Bounds { get; init; }

    public bool CropToBounds { get; init; }
}

// struct obs_sceneitem_crop, in pixels taken off each edge of the source before it is placed.
// Negative values are not an error and not honoured: libobs stores zero for them.
public readonly record struct ObsCrop(int Left, int Top, int Right, int Bottom);
