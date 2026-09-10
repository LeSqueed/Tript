// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Obs.IntegrationTests;

public sealed class ObsSceneItemOrderTests
{
    private const string ColourSourceId = "color_source";

    [SkippableFact]
    public void EachSourceAdded_LandsOnTopOfTheOnesBeforeIt()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("stack");
        using var bottom = Add(scene, "bottom");
        using var middle = Add(scene, "middle");
        using var top = Add(scene, "top");

        Assert.Equal(0, bottom.OrderPosition);
        Assert.Equal(1, middle.OrderPosition);
        Assert.Equal(2, top.OrderPosition);
    }

    [SkippableFact]
    public void Enumeration_RunsFromTheBottomOfTheSceneUpwards()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("enumerated stack");
        using var bottom = Add(scene, "bottom");
        using var middle = Add(scene, "middle");
        using var top = Add(scene, "top");

        var items = scene.EnumerateItems();

        Assert.Equal([0, 1, 2], items.Select(item => item.OrderPosition));
        Assert.Equal([bottom.Id, middle.Id, top.Id], items.Select(item => item.Id));

        foreach (var item in items)
            item.Dispose();
    }

    [SkippableFact]
    public void MovingAnItemToTheTop_GivesItTheHighestPosition()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("moved");
        using var first = Add(scene, "first");
        using var second = Add(scene, "second");
        using var third = Add(scene, "third");

        first.MoveInOrder(ObsOrderMovement.Top);

        Assert.Equal(2, first.OrderPosition);
        Assert.Equal(0, second.OrderPosition);
        Assert.Equal(1, third.OrderPosition);
    }

    [SkippableFact]
    public void MovingAnItemUpOrDown_ShiftsItByOne()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("nudged");
        using var first = Add(scene, "first");
        using var second = Add(scene, "second");
        using var third = Add(scene, "third");

        first.MoveInOrder(ObsOrderMovement.Up);
        Assert.Equal(1, first.OrderPosition);
        Assert.Equal(0, second.OrderPosition);

        third.MoveInOrder(ObsOrderMovement.Bottom);
        Assert.Equal(0, third.OrderPosition);
        Assert.Equal(2, first.OrderPosition);
        Assert.Equal(1, second.OrderPosition);
    }

    [SkippableFact]
    public void SettingAnOrderPosition_PutsTheItemExactlyThere()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("positioned");
        using var first = Add(scene, "first");
        using var second = Add(scene, "second");
        using var third = Add(scene, "third");

        third.OrderPosition = 0;

        Assert.Equal(0, third.OrderPosition);
        Assert.Equal(1, first.OrderPosition);
        Assert.Equal(2, second.OrderPosition);
    }

    [SkippableFact]
    public void ReorderingTheWholeScene_AppliesTheGivenOrderBottomFirst()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("reordered");
        using var first = Add(scene, "first");
        using var second = Add(scene, "second");
        using var third = Add(scene, "third");

        Assert.True(scene.Reorder([second, first, third]));

        Assert.Equal(0, second.OrderPosition);
        Assert.Equal(1, first.OrderPosition);
        Assert.Equal(2, third.OrderPosition);

        var items = scene.EnumerateItems();
        Assert.Equal([second.Id, first.Id, third.Id], items.Select(item => item.Id));
        foreach (var item in items)
            item.Dispose();
    }

    [SkippableFact]
    public void ReorderingWithAnIncompleteList_IsRefusedAndChangesNothing()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("partial");
        using var first = Add(scene, "first");
        using var second = Add(scene, "second");

        Assert.False(scene.Reorder([second]));

        Assert.Equal(0, first.OrderPosition);
        Assert.Equal(1, second.OrderPosition);
    }

    [SkippableFact]
    public void RemovingAnItem_RenumbersTheOnesAboveIt()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("gap");
        using var bottom = Add(scene, "bottom");
        using var middle = Add(scene, "middle");
        using var top = Add(scene, "top");

        middle.Remove();

        Assert.Equal(0, bottom.OrderPosition);
        Assert.Equal(1, top.OrderPosition);
    }

    private static ObsSceneItem Add(ObsScene scene, string sourceName)
    {
        using var source = ObsSource.CreatePrivate(ColourSourceId, sourceName);
        var item = scene.AddSource(source);
        Assert.NotNull(item);
        return item;
    }
}
