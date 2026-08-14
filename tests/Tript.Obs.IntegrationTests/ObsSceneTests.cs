// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Obs.IntegrationTests;

// Scenes, attachment and the output channel a scene is recorded through. Placement lives in
// ObsSceneItemTransformTests and z-order in ObsSceneItemOrderTests.
public sealed class ObsSceneTests
{
    private const string ColourSourceId = "color_source";
    private const string ScreenCaptureId = "xshm_input";

    [Fact]
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

    [Fact]
    public void ASceneAndItsSource_ConvertBackAndForth()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("round trip");

        using var source = scene.AsSource();
        using var again = ObsScene.FromSource(source);

        Assert.NotNull(again);
        Assert.Equal("round trip", again.Name);
    }

    [Fact]
    public void ASourceThatIsNotAScene_ConvertsToNothing()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var source = ObsSource.Create(ColourSourceId, "not a scene");

        Assert.Null(ObsScene.FromSource(source));
    }

    [Fact]
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

    [Fact]
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

    // The same source in two places at once is legitimate and gives two independent items.
    [Fact]
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

    // libobs refuses the cycle rather than recursing into it. The null is the whole report, so a
    // binding that treated it as impossible would turn a refusal into a NullReferenceException.
    [Fact]
    public void ASceneCannotContainItself()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("self");
        using var itsSource = scene.AsSource();

        Assert.Null(scene.AddSource(itsSource));
    }

    [Fact]
    public void EnumeratingAnEmptyScene_ReturnsNothing()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("empty");

        Assert.Empty(scene.EnumerateItems());
    }

    [Fact]
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

        // Disposing the enumerated handles must not take the items out of the scene: they are
        // references of their own, not the scene's.
        foreach (var item in items)
            item.Dispose();

        Assert.Equal(2, scene.EnumerateItems().Count);
        Assert.True(firstItem.IsAttached);
    }

    // The composition root: a scene on an output channel is what recording and preview both read.
    [Fact]
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

    // The channel takes a reference of its own, which is what makes "set it and forget it" safe.
    [Fact]
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

    [Fact]
    public void AChannelBeyondTheLast_IsRefused()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("out of range");

        Assert.Equal(64u, ObsRuntime.MaxOutputChannels);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Runtime.SetOutputSource(64, scene));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Runtime.GetOutputSource(64));
    }

    // The Linux capture source, which is what stands in here for the game capture this surface
    // exists to carry. It reports a real screen size, which a source that failed to open would not.
    [Fact]
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

        // Teardown in the order an application would use: clear the channel, detach, then let the
        // handles go.
        session.Runtime.SetOutputSource(0, (ObsSource?)null);
        item.Remove();
        session.Runtime.WaitForDestroyQueue();

        Assert.Empty(scene.EnumerateItems());
    }

    [Fact]
    public void ADisposedScene_RefusesFurtherUse()
    {
        using var session = ObsSession.StartWithSourceTypes();
        var scene = ObsScene.CreatePrivate("short lived");
        scene.Dispose();

        Assert.Throws<ObjectDisposedException>(() => scene.Name);

        // Disposing twice is not an error, which matters because the runtime disposes any scene the
        // caller forgot.
        scene.Dispose();
    }

    [Fact]
    public void AnEmptySceneName_IsRejected()
    {
        using var session = ObsSession.StartWithSourceTypes();

        Assert.Throws<ArgumentNullException>(() => ObsScene.CreatePrivate(null!));
        Assert.Throws<ArgumentException>(() => ObsScene.Create(string.Empty));
    }
}
