// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Obs.IntegrationTests;

public sealed class ObsSceneTests
{
    private const string ColourSourceId = "color_source";
    private const string ScreenCaptureId = "xshm_input";

    [SkippableFact]
    public void AScene_IsASourceOfSceneType()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("a scene");

        using var source = scene.AsSource();

        Assert.Equal(ObsSourceType.Scene, source.Type);
        Assert.True(source.IsScene);
        Assert.Equal("a scene", source.Name);
        Assert.Equal("a scene", scene.Name);
    }

    [SkippableFact]
    public void ASceneAndItsSource_ConvertBackAndForth()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("round trip");

        using var source = scene.AsSource();
        using var again = ObsScene.FromSource(source);

        Assert.NotNull(again);
        Assert.Equal("round trip", again.Name);
    }

    [SkippableFact]
    public void ASourceThatIsNotAScene_ConvertsToNothing()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var source = ObsSource.Create(ColourSourceId, "not a scene");

        Assert.Null(ObsScene.FromSource(source));
    }

    [SkippableFact]
    public void AddingASource_ReturnsAnItemBoundToThatSource()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("holder");
        using var source = ObsSource.Create(ColourSourceId, "attached");

        using var item = scene.AddSource(source);

        Assert.NotNull(item);
        Assert.True(item.IsAttached);
        Assert.Equal(1, item.Id);

        using var itemSource = item.GetSource();
        Assert.NotNull(itemSource);
        Assert.Equal(source.Uuid, itemSource.Uuid);
    }

    [SkippableFact]
    public void AnItem_IsFoundByItsSourceNameAndByItsId()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("searchable");
        using var source = ObsSource.Create(ColourSourceId, "findable item");
        using var added = scene.AddSource(source);
        Assert.NotNull(added);

        using var byName = scene.FindItem("findable item");
        using var byId = scene.FindItem(added.Id);

        Assert.NotNull(byName);
        Assert.NotNull(byId);
        Assert.Equal(added.Id, byName.Id);
        Assert.Equal(added.Id, byId.Id);
        Assert.Null(scene.FindItem("no such source"));
        Assert.Null(scene.FindItem(9999L));
    }

    [SkippableFact]
    public void AddingOneSourceTwice_MakesTwoItemsWithDistinctIds()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("twice");
        using var source = ObsSource.Create(ColourSourceId, "doubled");

        using var first = scene.AddSource(source);
        using var second = scene.AddSource(source);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, scene.EnumerateItems().Count);
    }

    [SkippableFact]
    public void ASceneCannotContainItself()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("self");
        using var itsSource = scene.AsSource();

        Assert.Null(scene.AddSource(itsSource));
    }

    [SkippableFact]
    public void EnumeratingAnEmptyScene_ReturnsNothing()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("empty");

        Assert.Empty(scene.EnumerateItems());
    }

    [SkippableFact]
    public void EnumerationReturnsEveryItem_AndEachIsAReferenceOfItsOwn()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("populated");
        using var first = ObsSource.Create(ColourSourceId, "first");
        using var second = ObsSource.Create(ColourSourceId, "second");
        using var firstItem = scene.AddSource(first);
        using var secondItem = scene.AddSource(second);

        var items = scene.EnumerateItems();

        Assert.Equal(2, items.Count);
        Assert.Equal([firstItem!.Id, secondItem!.Id], items.Select(item => item.Id));

        foreach (var item in items)
            item.Dispose();

        Assert.Equal(2, scene.EnumerateItems().Count);
        Assert.True(firstItem.IsAttached);
    }

    [SkippableFact]
    public void ASceneOnAnOutputChannel_IsReadBackAsThatScene()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("programme");
        using var expected = scene.AsSource();

        session.Runtime.SetOutputSource(0, scene);

        using (var actual = session.Runtime.GetOutputSource(0))
        {
            Assert.NotNull(actual);
            Assert.Equal(expected.Uuid, actual.Uuid);
        }

        session.Runtime.SetOutputSource(0, (ObsSource?)null);
        Assert.Null(session.Runtime.GetOutputSource(0));
    }

    [SkippableFact]
    public void AnOutputChannel_HoldsItsSourceEvenAfterTheCallerLetsGo()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var source = ObsSource.Create(ColourSourceId, "on a channel");
        using var weak = source.CreateWeakReference();
        session.Runtime.SetOutputSource(3, source);
        source.Dispose();
        session.Runtime.WaitForDestroyQueue();

        Assert.False(weak.IsExpired);

        session.Runtime.SetOutputSource(3, (ObsSource?)null);
        session.Runtime.WaitForDestroyQueue();

        Assert.True(weak.IsExpired);
    }

    [SkippableFact]
    public void AChannelBeyondTheLast_IsRefused()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("out of range");

        Assert.Equal(64u, ObsRuntime.MaxOutputChannels);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Runtime.SetOutputSource(64, scene));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Runtime.GetOutputSource(64));
    }

    [SkippableFact]
    public void ScreenCapture_AttachesToASceneAndReportsTheScreenSize()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("capture scene");

        using var settings = new ObsSettings();
        settings.SetInt("screen", 0);
        using var capture = ObsSource.Create(ScreenCaptureId, "screen capture", settings);
        using var item = scene.AddSource(capture);

        Assert.NotNull(item);
        Assert.True(capture.Width > 0, "the screen capture source reported no width");
        Assert.True(capture.Height > 0, "the screen capture source reported no height");

        session.Runtime.SetOutputSource(0, scene);
        using (var programme = session.Runtime.GetOutputSource(0))
        {
            Assert.NotNull(programme);
        }

        session.Runtime.SetOutputSource(0, (ObsSource?)null);
        item.Remove();
        session.Runtime.WaitForDestroyQueue();

        Assert.Empty(scene.EnumerateItems());
    }

    [SkippableFact]
    public void ADisposedScene_RefusesFurtherUse()
    {
        using var session = ObsSession.StartWithSourceTypes();
        var scene = ObsScene.CreatePrivate("short lived");
        scene.Dispose();

        Assert.Throws<ObjectDisposedException>(() => scene.Name);

        scene.Dispose();
    }

    [SkippableFact]
    public void AnEmptySceneName_IsRejected()
    {
        using var session = ObsSession.StartWithSourceTypes();

        Assert.Throws<ArgumentNullException>(() => ObsScene.CreatePrivate(null!));
        Assert.Throws<ArgumentException>(() => ObsScene.Create(string.Empty));
    }
}
