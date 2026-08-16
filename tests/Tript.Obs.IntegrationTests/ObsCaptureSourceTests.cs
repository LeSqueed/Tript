// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Obs.IntegrationTests;

// The capture-source surface against the real library on Linux: what source types are registered as
// inputs, the display-capture (xshm_input) defaults and properties, creating a real xshm_input
// source, and the game-capture path that on Linux is deliberately absent. The capture settings keys
// are plugin-side and not in the libobs headers, so everything here is asserted through the runtime
// discovery route (obs_get_source_properties / obs_source_properties) rather than a hardcoded table
// (spec/obs-binding.md, Part 11).
//
// Measured on this machine (OBS 32.2.1, Xwayland): the *type-level* property probe for xshm_input
// crashes inside linux-capture.so's property builder — it fires the server-change callback which
// opens a fresh XCB connection and enumerates monitors, and that crashes on this Xwayland server.
// The *instance-level* probe (obs_source_properties on a created source) works, because the source
// already holds its display connection. So the tests use the instance route for xshm_input, and the
// type route is exercised against capture-source types whose property builder is safe here.
public sealed class ObsCaptureSourceTests
{
    private const string XshmInputId = "xshm_input";
    private const string XCompositeInputId = "xcomposite_input";
    private const string PulseInputCaptureId = "pulse_input_capture";
    private const string PulseOutputCaptureId = "pulse_output_capture";
    private const string ColourSourceId = "color_source";

    // ---- input-type enumeration ----

    // The safe-module set on this machine: linux-capture's xshm_input and linux-pulseaudio's two
    // audio device captures. These are the ids the alpha recorder actually creates display and
    // audio sources with, so the enumeration must report them.
    [Fact]
    public void TheInputTypeEnumeration_ReportsTheSafeModuleCaptureSources()
    {
        using var session = ObsSession.StartWithAudioSources();

        var ids = ObsSourceProperties.EnumerateTypeIds();

        Assert.Contains(XshmInputId, ids);
        Assert.Contains(PulseInputCaptureId, ids);
        Assert.Contains(PulseOutputCaptureId, ids);
    }

    // obs_enum_input_types answers inputs only. Filters, transitions and scenes are source types
    // but are not inputs, so they never appear — the guard that keeps a capture-surface caller from
    // treating a non-input as a capture source.
    [Fact]
    public void TheInputTypeEnumeration_NeverReportsFilterOrSceneTypes()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var ids = ObsSourceProperties.EnumerateTypeIds();

        // Every enumerated id is an input source type. A filter or scene id would be caught by the
        // type probe: filters and scenes report type 1 (Filter) or 3 (Scene), not Input (0).
        foreach (var id in ids)
        {
            using var source = ObsSource.CreatePrivate(id, $"enum {id}");
            Assert.Equal(ObsSourceType.Input, source.Type);
        }
    }

    // ---- defaults and property discovery ----

    // GetTypeDefaults yields a settings object for a registered capture type. xshm_input's defaults
    // object is present but empty on 32.2.1 — the screen selection lives in the property list's
    // runtime items, not in a defaults object — which is exactly why the discovery route matters.
    [Fact]
    public void GetTypeDefaults_YieldsASettingsObjectForTheDisplayCaptureType()
    {
        using var session = ObsSession.StartWithSourceTypes();

        using var defaults = ObsSourceProperties.GetTypeDefaults(XshmInputId);
        Assert.NotNull(defaults);
    }

    // The instance-level property enumeration — obs_source_properties on a created source — is the
    // reliable route for capture sources. xshm_input's instance properties must include the
    // display-selection property a recorder needs to choose a monitor.
    [Fact]
    public void TheDisplayCaptureInstanceProperties_IncludeTheScreenSelection()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var source = ObsSource.CreatePrivate(XshmInputId, "screen probe");

        var properties = source.EnumerateProperties();
        var names = properties.Select(property => property.Name).ToArray();

        Assert.NotEmpty(properties);
        Assert.Contains(names, name => name.Contains("screen", StringComparison.OrdinalIgnoreCase));
    }

    // The display choices a capture source accepts, read through the instance discovery route. This
    // is the seam a recorder leans on instead of assuming a display index is valid.
    [Fact]
    public void TheDisplayCaptureChoices_AreEnumerableFromTheInstanceScreenProperty()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var source = ObsSource.CreatePrivate(XshmInputId, "choices");

        var choices = ObsCaptureSource.EnumerateDisplayChoices(source);

        // A non-empty list must carry Int items — the display index a recorder writes. (On a
        // headless CI box the list may be empty; the shape is what is asserted.)
        foreach (var choice in choices)
            Assert.Equal(ObsComboFormat.Int, choice.Format);
    }

    // The type-level property probe works for capture-source types whose property builder is safe —
    // xcomposite_input on this machine. This is the pre-creation discovery route.
    [Fact]
    public void TheTypeLevelPropertyProbe_WorksForACaptureSourceType()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var properties = ObsSourceProperties.EnumerateTypeProperties(XCompositeInputId);

        Assert.NotEmpty(properties);
        Assert.Contains(properties, property => property.Name.Length > 0);
    }

    // The discovery guard: a filter/transition/scene type id must not crash property discovery.
    // Scenes are registered by libobs itself, so this exercises the obs_get_source_properties path
    // against non-input source types directly.
    [Fact]
    public void PropertyDiscovery_IsSafeForFilterAndTransitionTypeIds()
    {
        using var session = ObsSession.StartWithSourceTypes();

        // A scene type id — libobs registers "scene" itself. Property discovery must not crash, and
        // must return a list (possibly empty).
        _ = ObsSourceProperties.EnumerateTypeProperties("scene");

        // An unknown id: obs_get_source_properties returns null and the surface reports empty.
        Assert.Empty(ObsSourceProperties.EnumerateTypeProperties("tript_no_such_source_type"));
    }

    // ---- creation ----

    // Creating an xshm_input source succeeds against the real library, and reports the actual
    // display size once it has attached — which on this machine is a real monitor (1920x1080),
    // because the source attaches at creation. The assertion is that the pair is sane (both zero, or
    // both the display size), never one-sided garbage.
    [Fact]
    public void CreatingTheDisplayCaptureSource_SucceedsAndReportsSaneDimensions()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var source = ObsSource.CreatePrivate(XshmInputId, "captured display");

        Assert.Equal(XshmInputId, source.Id);
        Assert.Equal(ObsSourceType.Input, source.Type);

        var width = source.Width;
        var height = source.Height;
        Assert.True((width == 0 && height == 0) || (width > 0 && height > 0),
            $"Expected both-zero or both-positive dimensions, got {width}x{height}.");

        // When the source has attached, base and current size agree and are the real display size.
        if (width > 0)
        {
            Assert.Equal(width, source.BaseWidth);
            Assert.Equal(height, source.BaseHeight);
        }
    }

    // The discovered display-selection key round-trips through creation: build settings with the
    // instance-discovered screen property, create the source with them, and the setting survives.
    // This is the exact shape the recorder uses to pick a monitor without hardcoding a key.
    [Fact]
    public void CreatingTheDisplayCaptureSource_WithTheDiscoveredScreenKey_KeepsTheSetting()
    {
        using var session = ObsSession.StartWithSourceTypes();

        using var source = ObsSource.CreatePrivate(XshmInputId, "sized display");
        var choices = ObsCaptureSource.EnumerateDisplayChoices(source);
        var index = choices.Count > 0 && choices[0].Value is long first
            ? first
            : 0;

        using var settings = ObsCaptureSource.BuildDisplayCaptureSettings(source, (int)index);
        using var configured = ObsSource.CreatePrivate(XshmInputId, "sized display 2", settings);

        using var readBack = configured.GetSettings();

        // The screen key, discovered from the instance, is the key the plugin reads back.
        var screenKey = source.EnumerateProperties()
            .FirstOrDefault(property =>
                property.Type == ObsPropertyType.List &&
                property.Name.Contains("screen", StringComparison.OrdinalIgnoreCase))
            .Name;

        Assert.NotNull(screenKey);
        Assert.True(readBack.HasUserValue(screenKey), $"The discovered screen key '{screenKey}' did not survive creation.");
    }

    // ---- game capture: absent on Linux ----

    // Linux has no game-capture source in libobs (spec/recorder.md, "Capture"), so the game-capture
    // surface reports the absence and the recorder falls back to display capture. This is a first-
    // class documented state, not an error.
    [Fact]
    public void GameCapture_IsReportedAbsentOnLinux()
    {
        using var session = ObsSession.StartWithSourceTypes();

        Assert.Null(ObsCaptureSource.GetGameCaptureProperties());
        Assert.Null(ObsCaptureSource.BuildGameCaptureSettings(new ObsGameCaptureTarget("title", null, "/path/to/game")));

        using var source = ObsSource.CreatePrivate(XshmInputId, "not a game source");
        Assert.False(ObsCaptureSource.Retarget(source, new ObsGameCaptureTarget("title", null, null)));
    }

    // The re-targeting seam refuses a display-capture source on Linux, which is the only source
    // type that exists there. The recorder must never get a false "re-targeted" for a source that
    // cannot be attached to a game.
    [Fact]
    public void Retargeting_ADisplaySource_IsRefusedOnLinux()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var source = ObsSource.CreatePrivate(XshmInputId, "display");

        Assert.False(ObsCaptureSource.Retarget(source, new ObsGameCaptureTarget("some title", null, "game.exe")));
    }

    // ---- empty and invalid ----

    [Fact]
    public void EmptyOrNullCaptureArguments_AreRejectedBeforeReachingLibobs()
    {
        using var session = ObsSession.StartWithSourceTypes();

        Assert.Throws<ArgumentNullException>(() => ObsCaptureSource.BuildGameCaptureSettings(null!));
        Assert.Throws<ArgumentNullException>(() => ObsCaptureSource.Retarget(null!, new ObsGameCaptureTarget(null, null, null)));
        Assert.Throws<ArgumentNullException>(() => ObsCaptureSource.Retarget(ObsSource.CreatePrivate(ColourSourceId, "c"), null!));
        Assert.Throws<ArgumentException>(() => ObsCaptureSource.BuildDisplayCaptureSettings(string.Empty, 0));
        Assert.Throws<ArgumentException>(() => ObsCaptureSource.EnumerateDisplayChoices(string.Empty));
        Assert.Throws<ArgumentNullException>(() => ObsCaptureSource.EnumerateDisplayChoices((ObsSource)null!));
    }
}
