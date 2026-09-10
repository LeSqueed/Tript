// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Numerics;

namespace Tript.Obs;

public sealed record ObsTransform
{
    public Vector2 Position { get; init; }

    public float Rotation { get; init; }

    public Vector2 Scale { get; init; } = Vector2.One;

    public ObsAlignment Alignment { get; init; } = ObsAlignment.Left | ObsAlignment.Top;

    public ObsBoundsType BoundsType { get; init; }

    public ObsAlignment BoundsAlignment { get; init; }

    public Vector2 Bounds { get; init; }

    public bool CropToBounds { get; init; }
}

public readonly record struct ObsCrop(int Left, int Top, int Right, int Bottom);
