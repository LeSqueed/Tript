// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Numerics;
using Xunit;

namespace Tript.Obs.IntegrationTests;

public sealed class ObsSceneItemTransformTests
{
    private const string ColourSourceId = "color_source";

    [SkippableFact]
    public void AFreshlyAttachedItem_HasUnitScaleAndTopLeftAlignmentAndNoBounds()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("defaults");
        using var source = ObsSource.Create(ColourSourceId, "default placement");
        using var item = scene.AddSource(source);
        Assert.NotNull(item);

        Assert.Equal(Vector2.Zero, item.Position);
        Assert.Equal(Vector2.One, item.Scale);
        Assert.Equal(0f, item.Rotation);
        Assert.Equal(ObsAlignment.Left | ObsAlignment.Top, item.Alignment);
        Assert.Equal(ObsBoundsType.None, item.BoundsType);
        Assert.Equal(ObsAlignment.Center, item.BoundsAlignment);
        Assert.Equal(Vector2.Zero, item.Bounds);
        Assert.False(item.CropToBounds);
        Assert.Equal(new ObsCrop(0, 0, 0, 0), item.Crop);
        Assert.True(item.IsVisible);
        Assert.False(item.IsLocked);
        Assert.False(item.IsSelected);
        Assert.Equal(ObsScaleType.Disable, item.ScaleFilter);
        Assert.Equal(ObsBlendingMethod.Default, item.BlendingMethod);
        Assert.Equal(ObsBlendingType.Normal, item.BlendingMode);
    }

    [SkippableFact]
    public void PositionScaleAndRotation_ReadBackAsSet()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("geometry");
        using var source = ObsSource.Create(ColourSourceId, "placed");
        using var item = scene.AddSource(source);
        Assert.NotNull(item);

        item.Position = new Vector2(12.5f, -30.5f);
        item.Scale = new Vector2(2.25f, 0.125f);
        item.Rotation = 33.5f;
        item.Alignment = ObsAlignment.Right | ObsAlignment.Bottom;

        Assert.Equal(new Vector2(12.5f, -30.5f), item.Position);
        Assert.Equal(new Vector2(2.25f, 0.125f), item.Scale);
        Assert.Equal(33.5f, item.Rotation);
        Assert.Equal(ObsAlignment.Right | ObsAlignment.Bottom, item.Alignment);
    }

    [SkippableFact]
    public void Rotation_IsKeptInDegreesWithoutWrappingOrRounding()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("rotation");
        using var source = ObsSource.Create(ColourSourceId, "rotated");
        using var item = scene.AddSource(source);
        Assert.NotNull(item);

        item.Rotation = 400.75f;
        Assert.Equal(400.75f, item.Rotation);

        item.Rotation = -12.25f;
        Assert.Equal(-12.25f, item.Rotation);

        item.Rotation = 33.333332f;
        Assert.Equal(33.333332f, item.Rotation);
    }

    [SkippableFact]
    public void ANegativeScale_IsStoredAsGiven()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("mirror");
        using var source = ObsSource.Create(ColourSourceId, "mirrored");
        using var item = scene.AddSource(source);
        Assert.NotNull(item);

        item.Scale = new Vector2(-1f, 1f);

        Assert.Equal(new Vector2(-1f, 1f), item.Scale);
    }

    [SkippableTheory]
    [InlineData(100.125f, 100.0f)]
    [InlineData(200.375f, 200.5f)]
    [InlineData(0.625f, 0.5f)]
    [InlineData(-7.875f, -8.0f)]
    [InlineData(1234.5678f, 1234.5f)]
    public void APositionOffTheHalfUnitGrid_IsSnappedToIt(float requested, float expected)
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("snapping");
        using var source = ObsSource.Create(ColourSourceId, "snapped");
        using var item = scene.AddSource(source);
        Assert.NotNull(item);

        item.Position = new Vector2(requested, requested);
        var actual = item.Position;

        Assert.Equal(expected, actual.X);
        Assert.Equal(expected, actual.Y);
        Assert.Equal(0f, actual.X * 2f % 1f);
    }

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
        item.CropToBounds = true;

        Assert.Equal(ObsBoundsType.ScaleInner, item.BoundsType);
        Assert.Equal(ObsAlignment.Top | ObsAlignment.Left, item.BoundsAlignment);
        Assert.Equal(new Vector2(640f, 360.5f), item.Bounds);
        Assert.True(item.CropToBounds);
    }

    [SkippableFact]
    public void BoundsOffTheHalfUnitGrid_AreSnappedToIt()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("bounds snapping");
        using var source = ObsSource.Create(ColourSourceId, "bounded oddly");
        using var item = scene.AddSource(source);
        Assert.NotNull(item);

        item.BoundsType = ObsBoundsType.Stretch;
        item.Bounds = new Vector2(1280.125f, 720.875f);

        Assert.Equal(new Vector2(1280f, 721f), item.Bounds);
    }

    [SkippableFact]
    public void AnUndefinedBoundsTypeOrAlignment_IsStoredRatherThanRejected()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("unvalidated");
        using var source = ObsSource.Create(ColourSourceId, "odd values");
        using var item = scene.AddSource(source);
        Assert.NotNull(item);

        item.BoundsType = (ObsBoundsType)99;
        item.Alignment = (ObsAlignment)0xFFFFFFFF;

        Assert.Equal((ObsBoundsType)99, item.BoundsType);
        Assert.Equal((ObsAlignment)0xFFFFFFFF, item.Alignment);
    }

    [SkippableFact]
    public void Crop_ReadsBackPerEdge()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("cropping");
        using var source = ObsSource.Create(ColourSourceId, "cropped");
        using var item = scene.AddSource(source);
        Assert.NotNull(item);

        item.Crop = new ObsCrop(1, 2, 3, 4);

        Assert.Equal(new ObsCrop(1, 2, 3, 4), item.Crop);
    }

    [SkippableFact]
    public void ANegativeCrop_IsStoredAsZero()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("negative crop");
        using var source = ObsSource.Create(ColourSourceId, "cropped oddly");
        using var item = scene.AddSource(source);
        Assert.NotNull(item);

        item.Crop = new ObsCrop(-5, -6, 7, 8);

        Assert.Equal(new ObsCrop(0, 0, 7, 8), item.Crop);
    }

    [SkippableFact]
    public void TheWholeTransform_RoundTripsThroughOneStruct()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("transform");
        using var source = ObsSource.Create(ColourSourceId, "transformed");
        using var item = scene.AddSource(source);
        Assert.NotNull(item);

        var expected = new ObsTransform
        {
            Position = new Vector2(5.5f, 6.5f),
            Rotation = 45f,
            Scale = new Vector2(3f, 4f),
            Alignment = ObsAlignment.Center,
            BoundsType = ObsBoundsType.ScaleOuter,
            BoundsAlignment = ObsAlignment.Bottom,
            Bounds = new Vector2(100f, 200f),
            CropToBounds = true
        };

        item.Transform = expected;

        Assert.Equal(expected, item.Transform);

        Assert.Equal(expected.Position, item.Position);
        Assert.Equal(expected.Rotation, item.Rotation);
        Assert.Equal(expected.Scale, item.Scale);
        Assert.Equal(expected.Alignment, item.Alignment);
        Assert.Equal(expected.BoundsType, item.BoundsType);
        Assert.Equal(expected.BoundsAlignment, item.BoundsAlignment);
        Assert.Equal(expected.Bounds, item.Bounds);
        Assert.True(item.CropToBounds);
    }

    [SkippableFact]
    public void TheDefaultTransform_MatchesWhatAFreshItemReports()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("default transform");
        using var source = ObsSource.Create(ColourSourceId, "untouched");
        using var item = scene.AddSource(source);
        Assert.NotNull(item);

        Assert.Equal(new ObsTransform(), item.Transform);
    }

    [SkippableFact]
    public void DeferredUpdates_DoNotHideTheValuesBeingSet()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("deferred");
        using var source = ObsSource.Create(ColourSourceId, "deferred placement");
        using var item = scene.AddSource(source);
        Assert.NotNull(item);

        using (item.DeferUpdates())
        {
            item.Position = new Vector2(42f, 43f);
            Assert.Equal(new Vector2(42f, 43f), item.Position);
        }

        item.ForceUpdateTransform();
        Assert.Equal(new Vector2(42f, 43f), item.Position);
    }

    [SkippableFact]
    public void SettingVisibilityOrLock_ReportsWhetherItChangedAnything()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("visibility");
        using var source = ObsSource.Create(ColourSourceId, "hidden");
        using var item = scene.AddSource(source);
        Assert.NotNull(item);

        Assert.True(item.SetVisible(false));
        Assert.False(item.IsVisible);
        Assert.False(item.SetVisible(false));
        Assert.True(item.SetVisible(true));
        Assert.True(item.IsVisible);

        Assert.True(item.SetLocked(true));
        Assert.True(item.IsLocked);
        Assert.False(item.SetLocked(true));
    }

    [SkippableFact]
    public void ScaleFilterAndBlending_ReadBackAsSet()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("rendering");
        using var source = ObsSource.Create(ColourSourceId, "blended");
        using var item = scene.AddSource(source);
        Assert.NotNull(item);

        item.ScaleFilter = ObsScaleType.Lanczos;
        item.BlendingMethod = ObsBlendingMethod.SrgbOff;
        item.BlendingMode = ObsBlendingType.Multiply;

        Assert.Equal(ObsScaleType.Lanczos, item.ScaleFilter);
        Assert.Equal(ObsBlendingMethod.SrgbOff, item.BlendingMethod);
        Assert.Equal(ObsBlendingType.Multiply, item.BlendingMode);
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
