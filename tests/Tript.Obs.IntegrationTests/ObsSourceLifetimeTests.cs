// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Obs.IntegrationTests;

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

        using (var stillThere = item.GetSource())
        {
            Assert.NotNull(stillThere);
            Assert.Equal("detached", stillThere.Name);
        }

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

        Assert.InRange(ObsRuntime.LiveAllocationCount - before, 0, 4);
    }

    [SkippableFact]
    public void RepeatedAttachAndDetachCycles_LeakNothing()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var scene = ObsScene.CreatePrivate("recycled");

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

    [SkippableFact]
    public void AHandleThatOutlivesTheContext_DeclinesToRelease()
    {
        var session = ObsSession.StartWithSourceTypes();
        var source = ObsSource.Create(ColourSourceId, "outlives the context");
        var scene = ObsScene.CreatePrivate("also outlives");

        session.Dispose();

        source.Dispose();
        scene.Dispose();
    }

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

    [SkippableFact]
    public void ASceneStillHoldingItemsAtShutdown_DoesNotTakeTheProcessDown()
    {
        var session = ObsSession.StartWithSourceTypes();

        var scene = ObsScene.CreatePrivate("never disposed");
        var source = ObsSource.Create(ColourSourceId, "never detached");
        var item = scene.AddSource(source);
        Assert.NotNull(item);

        session.Dispose();

        Assert.False(ObsRuntime.IsInitialized);
    }
}
