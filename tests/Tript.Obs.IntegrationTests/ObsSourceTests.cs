// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text;
using Xunit;

namespace Tript.Obs.IntegrationTests;

// Creating, configuring and identifying sources against the real library. Lifetime and reference
// counting are asserted separately, in ObsSourceLifetimeTests.
public sealed class ObsSourceTests
{
    private const string ColourSourceId = "color_source";
    private const string ScreenCaptureId = "xshm_input";

    [Fact]
    public void ACreatedSource_ReportsTheTypeIdAndNameItWasGiven()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var source = ObsSource.Create(ColourSourceId, "a colour");

        Assert.Equal(ColourSourceId, source.Id);
        Assert.Equal(ColourSourceId, source.UnversionedId);
        Assert.Equal("a colour", source.Name);
        Assert.Equal(ObsSourceType.Input, source.Type);
        Assert.False(source.IsScene);
    }

    // libobs copies the strings it is handed, which is the opposite of obs_reset_video and is why
    // nothing here is interned. The generated marshaller frees its UTF-8 buffer the moment the call
    // returns, so a retained pointer would be reading freed memory by the time this reads the name
    // back — and the name is built at run time so no literal can be sitting at that address.
    [Fact]
    public void SourceCreation_CopiesTheNameRatherThanKeepingTheCallersBuffer()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var name = new StringBuilder("copied ").Append("name ").Append(Random.Shared.Next(1000, 9999)).ToString();
        using var source = ObsSource.Create(ColourSourceId, name);

        // Churn the managed heap so that anything the marshaller left behind is unlikely to survive.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        _ = new byte[1 << 20];

        Assert.Equal(name, source.Name);
        Assert.Equal(ColourSourceId, source.Id);
    }

    [Fact]
    public void EverySource_GetsADistinctUuid()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var first = ObsSource.Create(ColourSourceId, "same name");
        using var second = ObsSource.Create(ColourSourceId, "same name");

        Assert.NotEmpty(first.Uuid);
        Assert.NotEqual(first.Uuid, second.Uuid);
    }

    // A name is not an identity: libobs accepts the duplicate and a lookup answers with the first.
    [Fact]
    public void TwoSourcesMayShareAName_AndTheLookupFindsTheOlder()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var first = ObsSource.Create(ColourSourceId, "duplicated");
        using var second = ObsSource.Create(ColourSourceId, "duplicated");

        using var found = ObsSource.FindByName("duplicated");

        Assert.NotNull(found);
        Assert.Equal(first.Uuid, found.Uuid);
    }

    [Fact]
    public void APrivateSource_IsNotFindableByName()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var source = ObsSource.CreatePrivate(ColourSourceId, "kept private");

        Assert.Null(ObsSource.FindByName("kept private"));
    }

    [Fact]
    public void AFindableSource_IsFoundByName()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var source = ObsSource.Create(ColourSourceId, "findable");

        using var found = ObsSource.FindByName("findable");

        Assert.NotNull(found);
        Assert.Equal(source.Uuid, found.Uuid);
    }

    // The one that would otherwise be discovered as a blank recording: libobs answers an
    // unregistered id with a placeholder source rather than with null, so null-checking the result
    // proves nothing.
    [Fact]
    public void AnUnregisteredSourceId_IsRefusedRatherThanGivenAPlaceholder()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var failure = Assert.Throws<ObsException>(() => ObsSource.Create("tript_no_such_source", "ghost"));

        Assert.Contains("tript_no_such_source", failure.Message, StringComparison.Ordinal);
        Assert.Null(ObsSource.FindByName("ghost"));
    }

    [Fact]
    public void ATypeDisplayName_IsPresentForRegisteredTypesAndAbsentForUnknownOnes()
    {
        using var session = ObsSession.StartWithSourceTypes();

        Assert.NotNull(ObsSource.GetTypeDisplayName(ColourSourceId));
        Assert.NotNull(ObsSource.GetTypeDisplayName(ScreenCaptureId));
        Assert.Null(ObsSource.GetTypeDisplayName("tript_no_such_source"));
    }

    [Fact]
    public void ScreenCapture_DeclaresItselfAVideoSourceThatDoesNotDuplicate()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var source = ObsSource.Create(ScreenCaptureId, "screen");

        Assert.True(source.OutputFlags.HasFlag(ObsSourceOutputFlags.Video));
        Assert.True(source.OutputFlags.HasFlag(ObsSourceOutputFlags.DoNotDuplicate));
        Assert.Equal(ObsSource.GetTypeOutputFlags(ScreenCaptureId), source.OutputFlags);
    }

    // The settings object is shared with the source rather than copied into it — measured, and the
    // reason a caller must not treat its own reference as private after creation.
    [Fact]
    public void SettingsGivenAtCreation_RemainTheSourcesOwnSettingsObject()
    {
        using var session = ObsSession.StartWithSourceTypes();

        using var settings = new ObsSettings();
        settings.SetInt("width", 100);
        settings.SetInt("height", 50);

        using var source = ObsSource.Create(ColourSourceId, "sized", settings);
        Assert.Equal(100u, source.Width);
        Assert.Equal(50u, source.Height);

        // Written through the caller's own reference, after creation, with no Update call.
        settings.SetInt("width", 640);

        using var readBack = source.GetSettings();
        Assert.Equal(640, readBack.GetInt("width"));
    }

    [Fact]
    public void UpdatingASource_ChangesTheSizeItReports()
    {
        using var session = ObsSession.StartWithSourceTypes();

        using var initial = new ObsSettings();
        initial.SetInt("width", 100);
        initial.SetInt("height", 50);
        using var source = ObsSource.Create(ColourSourceId, "resized", initial);

        using var update = new ObsSettings();
        update.SetInt("width", 321);
        update.SetInt("height", 123);
        source.Update(update);
        session.Runtime.WaitForDestroyQueue();

        Assert.Equal(321u, source.Width);
        Assert.Equal(123u, source.Height);
        Assert.Equal(321u, source.BaseWidth);
        Assert.Equal(123u, source.BaseHeight);
    }

    [Fact]
    public void ASourcesName_CanBeChangedAfterCreation()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var source = ObsSource.Create(ColourSourceId, "first name");

        source.Name = "second name";

        Assert.Equal("second name", source.Name);
        Assert.Null(ObsSource.FindByName("first name"));
        using var found = ObsSource.FindByName("second name");
        Assert.NotNull(found);
    }

    [Fact]
    public void ANewSource_IsEnabledAndNeitherActiveNorShowing()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var source = ObsSource.Create(ColourSourceId, "idle");

        Assert.True(source.IsEnabled);
        Assert.False(source.IsActive);
        Assert.False(source.IsShowing);
        Assert.False(source.IsRemoved);

        source.IsEnabled = false;
        Assert.False(source.IsEnabled);
    }

    // Marking a source removed destroys nothing and unlists nothing: it raises a flag and signals
    // whoever holds a reference to let go. The source is still there, and still findable, until they
    // do.
    [Fact]
    public void MarkingASourceRemoved_RaisesAFlagWithoutDestroyingOrUnlistingIt()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var source = ObsSource.Create(ColourSourceId, "to be removed");

        source.MarkRemoved();

        Assert.True(source.IsRemoved);
        Assert.Equal("to be removed", source.Name);
        using var stillFound = ObsSource.FindByName("to be removed");
        Assert.NotNull(stillFound);
    }

    [Fact]
    public void AnEmptyOrNullTypeId_IsRejectedBeforeItReachesLibobs()
    {
        using var session = ObsSession.StartWithSourceTypes();

        // Not a nicety: obs_source_create dereferences the id without checking it, so a null id
        // takes the process down rather than returning null.
        Assert.Throws<ArgumentNullException>(() => ObsSource.Create(null!, "named"));
        Assert.Throws<ArgumentException>(() => ObsSource.Create(string.Empty, "named"));
        Assert.Throws<ArgumentNullException>(() => ObsSource.Create(ColourSourceId, null!));
        Assert.Throws<ArgumentException>(() => ObsSource.Create(ColourSourceId, string.Empty));
    }

    [Fact]
    public void ADisposedSource_RefusesFurtherUse()
    {
        using var session = ObsSession.StartWithSourceTypes();
        var source = ObsSource.Create(ColourSourceId, "short lived");
        source.Dispose();

        Assert.Throws<ObjectDisposedException>(() => source.Name);
    }
}
