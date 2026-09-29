// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Numerics;
using Xunit;

namespace Tript.Obs.IntegrationTests;

public sealed class ObsSceneItemTransformTests
{
    private const string ColourSourceId = "color_source";

    [SkippableFact]
    public void BoundsTypeAndSize_ReadBackAsSet()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("bounds");
        using var source = ObsSource.Create(ColourSourceId, "bounded");
        using var item = scene.AddSource(source);
        Assert.NotNull(item);

        item.BoundsType = ObsBoundsType.ScaleInner;
        item.BoundsAlignment = ObsAlignment.Top | ObsAlignment.Left;
        item.Bounds = new Vector2(640f, 360.5f);

        Assert.Equal(ObsBoundsType.ScaleInner, item.BoundsType);
        Assert.Equal(ObsAlignment.Top | ObsAlignment.Left, item.BoundsAlignment);
        Assert.Equal(new Vector2(640f, 360.5f), item.Bounds);
    }

    [SkippableFact]
    public void TwoItemsOverOneSource_CarryIndependentTransforms()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("two placements");
        using var source = ObsSource.Create(ColourSourceId, "placed twice");
        using var first = scene.AddSource(source);
        using var second = scene.AddSource(source);
        Assert.NotNull(first);
        Assert.NotNull(second);

        first.Position = new Vector2(10f, 20f);
        second.Position = new Vector2(30f, 40f);

        Assert.Equal(new Vector2(10f, 20f), first.Position);
        Assert.Equal(new Vector2(30f, 40f), second.Position);
    }
}
