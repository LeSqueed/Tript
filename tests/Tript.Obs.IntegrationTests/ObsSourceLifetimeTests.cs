// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Obs.IntegrationTests;

// Who holds a reference to what, and what survives which teardown order. Everything here is
// asserted through two instruments libobs provides rather than through inspection: a weak
// reference, which expires exactly when its source is destroyed, and the live bmem allocation
// count.
public sealed class ObsSourceLifetimeTests
{
    private const string ColourSourceId = "color_source";

    [SkippableFact]
    public void ReleasingASource_DestroysIt()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var source = ObsSource.Create(ColourSourceId, "released");
        using var weak = source.CreateWeakReference();
        Assert.False(weak.IsExpired);

        source.Dispose();
        session.Runtime.WaitForDestroyQueue();

        Assert.True(weak.IsExpired);
        Assert.Null(weak.TryGetSource());
    }

    // A second owning reference is what obs_source_get_ref is for, and it does exactly what it says:
    // the source outlives the first handle's disposal.
    [SkippableFact]
    public void ASecondReference_KeepsTheSourceAliveAfterTheFirstIsDisposed()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var source = ObsSource.Create(ColourSourceId, "shared");
        var second = source.AddReference();
        using var weak = source.CreateWeakReference();

        source.Dispose();
        session.Runtime.WaitForDestroyQueue();
        Assert.False(weak.IsExpired);

        second.Dispose();
        session.Runtime.WaitForDestroyQueue();
        Assert.True(weak.IsExpired);
    }

    // The rule that matters for a recorder: attaching a source to a scene hands the scene a
    // reference of its own, so the caller may let go of its handle immediately. The holder is the
    // scene *item*, not the scene, which is the part a signature cannot show: detaching the item is
    // not enough while a handle to that item is still open.
    [SkippableFact]
    public void AttachingASourceToAScene_GivesTheSceneAReferenceOfItsOwn()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("holder");

        var source = ObsSource.Create(ColourSourceId, "attached");
        using var weak = source.CreateWeakReference();
        var item = scene.AddSource(source);
        Assert.NotNull(item);

        source.Dispose();
        session.Runtime.WaitForDestroyQueue();
        Assert.False(weak.IsExpired);

        item.Remove();
        session.Runtime.WaitForDestroyQueue();
        Assert.False(weak.IsExpired);

        item.Dispose();
        session.Runtime.WaitForDestroyQueue();
        Assert.True(weak.IsExpired);
    }

    [SkippableFact]
    public void DisposingTheScene_ReleasesTheSourcesItHolds()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var scene = ObsScene.CreatePrivate("disposable");
        var source = ObsSource.Create(ColourSourceId, "held by the scene");
        using var weak = source.CreateWeakReference();
        var item = scene.AddSource(source);
        Assert.NotNull(item);

        source.Dispose();
        item.Dispose();
        scene.Dispose();
        session.Runtime.WaitForDestroyQueue();

        Assert.True(weak.IsExpired);
    }

    // A scene item handle is a reference of its own, taken because the pointer libobs hands back
    // belongs to the scene. Removing the item from the scene therefore leaves this object usable,
    // just detached.
    [SkippableFact]
    public void ASceneItemHandle_StaysUsableAfterItIsRemovedFromItsScene()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("detaching");
        using var source = ObsSource.Create(ColourSourceId, "detached");

        using var item = scene.AddSource(source);
        Assert.NotNull(item);
        var id = item.Id;
        Assert.True(item.IsAttached);

        item.Remove();

        Assert.Equal(id, item.Id);
        Assert.False(item.IsAttached);
        Assert.Null(scene.FindItem("detached"));

        // Detached from the scene, but still holding its source: the item's reference to what it
        // places outlives its membership of a scene and is given up only when the item is.
        using (var stillThere = item.GetSource())
        {
            Assert.NotNull(stillThere);
            Assert.Equal("detached", stillThere.Name);
        }

        // Removing twice is not an error; the second call finds nothing to detach.
        item.Remove();
    }

    [SkippableTheory]
    [InlineData("source, item, scene")]
    [InlineData("item, scene, source")]
    [InlineData("scene, item, source")]
    [InlineData("scene, source, item")]
    public void AnyDisposalOrder_LeavesNothingBehind(string order)
    {
        using var session = ObsSession.StartWithSourceTypes();
        session.Runtime.WaitForDestroyQueue();
        var before = ObsRuntime.LiveAllocationCount;

        var scene = ObsScene.CreatePrivate($"order {order}");
        var source = ObsSource.Create(ColourSourceId, $"colour {order}");
        var item = scene.AddSource(source);
        Assert.NotNull(item);

        using var weak = source.CreateWeakReference();

        foreach (var step in order.Split(", "))
        {
            switch (step)
            {
                case "source":
                    source.Dispose();
                    break;
                case "item":
                    item.Dispose();
                    break;
                case "scene":
                    scene.Dispose();
                    break;
            }
        }

        session.Runtime.WaitForDestroyQueue();

        Assert.True(weak.IsExpired, $"the source survived disposal in the order: {order}");

        // The weak reference itself is still live, and it is a bmem allocation of its own.
        Assert.InRange(ObsRuntime.LiveAllocationCount - before, 0, 4);
    }

    // The cycle a recorder actually performs when the user switches what is being captured.
    [SkippableFact]
    public void RepeatedAttachAndDetachCycles_LeakNothing()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("recycled");

        // Warm-up cycles first: the first attachments of a source type allocate things the type then
        // keeps, and counting those as a leak would make the assertion meaningless.
        RunCycles(scene, 10);
        session.Runtime.WaitForDestroyQueue();
        var before = ObsRuntime.LiveAllocationCount;

        RunCycles(scene, 200);
        session.Runtime.WaitForDestroyQueue();

        Assert.Equal(before, ObsRuntime.LiveAllocationCount);
        Assert.Empty(scene.EnumerateItems());

        static void RunCycles(ObsScene scene, int count)
        {
            for (var i = 0; i < count; i++)
            {
                using var source = ObsSource.CreatePrivate(ColourSourceId, "cycled");
                using var item = scene.AddSource(source);
                Assert.NotNull(item);
                item.Remove();
            }
        }
    }

    // The measured asymmetry between the two ways of creating a scene: a findable one is held by the
    // OBS core as well as by its caller, so disposal has to make the core let go too. Without that
    // this test finds the scene still there.
    [SkippableFact]
    public void DisposingAFindableScene_LeavesItNeitherFindableNorAlive()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var scene = ObsScene.Create("findable scene");
        using var weak = WeakReferenceTo(scene);

        using (var found = ObsSource.FindByName("findable scene"))
        {
            Assert.NotNull(found);
        }

        scene.Dispose();
        session.Runtime.WaitForDestroyQueue();

        Assert.Null(ObsSource.FindByName("findable scene"));
        Assert.True(weak.IsExpired);
    }

    // The intermediate source reference has to be gone before the scene's lifetime is asserted on,
    // or it is this test keeping the scene alive.
    private static ObsWeakSource WeakReferenceTo(ObsScene scene)
    {
        using var source = scene.AsSource();
        return source.CreateWeakReference();
    }

    [SkippableFact]
    public void APrivateScene_IsNeverFindableByName()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("private scene");

        Assert.Null(ObsSource.FindByName("private scene"));
    }

    // libobs frees every source at shutdown whether or not a caller still holds a reference, so the
    // pointer in a handle that outlives the context refers to nothing. Releasing it would be a
    // use-after-free; the handle has to notice and decline.
    [SkippableFact]
    public void AHandleThatOutlivesTheContext_DeclinesToRelease()
    {
        var session = ObsSession.StartWithSourceTypes();
        var source = ObsSource.Create(ColourSourceId, "outlives the context");
        var scene = ObsScene.CreatePrivate("also outlives");

        session.Dispose();

        // Both of these would be a release against freed memory if the handle did not check.
        source.Dispose();
        scene.Dispose();
    }

    // A weak reference is the opposite case, and for the same reason as a settings object: its
    // control block is bmem's, it survives obs_shutdown, and declining to release it would leak it.
    [SkippableFact]
    public void AWeakReferenceDisposedAfterTheContextIsGone_IsReleasedRatherThanLeaked()
    {
        ObsWeakSource weak;

        var session = ObsSession.StartWithSourceTypes();
        using (var source = ObsSource.Create(ColourSourceId, "weakly held"))
        {
            weak = source.CreateWeakReference();
        }

        session.Runtime.WaitForDestroyQueue();
        session.Dispose();

        var before = ObsRuntime.LiveAllocationCount;
        weak.Dispose();

        Assert.True(ObsRuntime.LiveAllocationCount < before,
            "disposing a weak reference after shutdown should still free its control block");
    }

    // Measured on 32.2.1 and documented nowhere: obs_shutdown crashes — a segmentation fault inside
    // libobs, not a leak — when a scene the caller still references still has items attached. This
    // test leaks exactly that arrangement on purpose.
    [SkippableFact]
    public void ASceneStillHoldingItemsAtShutdown_DoesNotTakeTheProcessDown()
    {
        var session = ObsSession.StartWithSourceTypes();

        var scene = ObsScene.CreatePrivate("never disposed");
        var source = ObsSource.Create(ColourSourceId, "never detached");
        var item = scene.AddSource(source);
        Assert.NotNull(item);

        // Deliberately no disposal of the scene, the item or the source.
        session.Dispose();

        Assert.False(ObsRuntime.IsInitialized);
    }
}
