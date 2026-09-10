// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Obs.IntegrationTests;

public sealed class ObsCaptureSourceTests
{
    private const string XshmInputId = "xshm_input";
    private const string ImageSourceId = "image_source";
    private const string PulseInputCaptureId = "pulse_input_capture";
    private const string PulseOutputCaptureId = "pulse_output_capture";
    private const string ColourSourceId = "color_source";

    [SkippableFact]
    public void TheInputTypeEnumeration_ReportsTheSafeModuleCaptureSources()
    {
        using var session = ObsSession.StartWithAudioSources();

        var ids = ObsSourceProperties.EnumerateTypeIds();

        Assert.Contains(XshmInputId, ids);
        Assert.Contains(PulseInputCaptureId, ids);
        Assert.Contains(PulseOutputCaptureId, ids);
    }

    [SkippableFact]
    public void TheInputTypeEnumeration_NeverReportsFilterOrSceneTypes()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var ids = ObsSourceProperties.EnumerateTypeIds();

        foreach (var id in ids)
        {
            using var source = ObsSource.CreatePrivate(id, $"enum {id}");
            Assert.Equal(ObsSourceType.Input, source.Type);
        }
    }

    [SkippableFact]
    public void GetTypeDefaults_YieldsASettingsObjectForTheDisplayCaptureType()
    {
        using var session = ObsSession.StartWithSourceTypes();

        using var defaults = ObsSourceProperties.GetTypeDefaults(XshmInputId);
        Assert.NotNull(defaults);
    }

    [SkippableFact]
    public void TheDisplayCaptureInstanceProperties_IncludeTheScreenSelection()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var source = ObsSource.CreatePrivate(XshmInputId, "screen probe");

        var properties = source.EnumerateProperties();
        var names = properties.Select(property => property.Name).ToArray();

        Assert.NotEmpty(properties);
        Assert.Contains(names, name => name.Contains("screen", StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public void TheDisplayCaptureChoices_AreEnumerableFromTheInstanceScreenProperty()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var source = ObsSource.CreatePrivate(XshmInputId, "choices");

        var choices = ObsCaptureSource.EnumerateDisplayChoices(source);

        foreach (var choice in choices)
            Assert.Equal(ObsComboFormat.Int, choice.Format);
    }

    [SkippableFact]
    public void TheTypeLevelPropertyProbe_WorksForATypeWhosePropertyBuilderNeedsNoInstance()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var properties = ObsSourceProperties.EnumerateTypeProperties(ImageSourceId);

        Assert.NotEmpty(properties);
        Assert.Contains(properties, property => property.Name.Length > 0);
    }

    [SkippableFact]
    public void PropertyDiscovery_IsSafeForFilterAndTransitionTypeIds()
    {
        using var session = ObsSession.StartWithSourceTypes();

        _ = ObsSourceProperties.EnumerateTypeProperties("scene");

        Assert.Empty(ObsSourceProperties.EnumerateTypeProperties("tript_no_such_source_type"));
    }

    [SkippableFact]
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

        if (width > 0)
        {
            Assert.Equal(width, source.BaseWidth);
            Assert.Equal(height, source.BaseHeight);
        }
    }

    [SkippableFact]
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

        var screenKey = source.EnumerateProperties()
            .FirstOrDefault(property =>
                property.Type == ObsPropertyType.List &&
                property.Name.Contains("screen", StringComparison.OrdinalIgnoreCase))
            .Name;

        Assert.NotNull(screenKey);
        Assert.True(readBack.HasUserValue(screenKey), $"The discovered screen key '{screenKey}' did not survive creation.");
    }

    [SkippableFact]
    public void TheDisplayCaptureId_IsDiscoveredFromTheRegisteredInputTypes()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var id = ObsCaptureSource.FindDisplayCaptureId();

        Assert.Equal(XshmInputId, id);
        Assert.Contains(id, ObsSourceProperties.EnumerateTypeIds());
    }

    [SkippableFact]
    public void TheDiscoveredDisplayCaptureId_CreatesARealSource()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var id = ObsCaptureSource.FindDisplayCaptureId();
        Assert.NotNull(id);

        using var source = ObsSource.CreatePrivate(id, "fallback layer");
        Assert.Equal(id, source.Id);
        Assert.Equal(ObsSourceType.Input, source.Type);
    }

    [SkippableFact]
    public void TheDisplayCaptureFallback_KeepsTheSelectionItWasUpdatedWith()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var id = ObsCaptureSource.FindDisplayCaptureId();
        Assert.NotNull(id);

        using var source = ObsSource.CreatePrivate(id, "fallback layer");
        using (var settings = ObsCaptureSource.BuildDisplayCaptureSettings(source, 0))
            source.Update(settings);

        using var readBack = source.GetSettings();
        var selectionKey = source.EnumerateProperties()
            .First(property =>
                property.Type == ObsPropertyType.List &&
                property.Name.Contains("screen", StringComparison.OrdinalIgnoreCase))
            .Name;

        Assert.True(readBack.HasUserValue(selectionKey), $"The discovered key '{selectionKey}' did not survive the update.");
    }

    [SkippableFact]
    public void GameCapture_IsReportedAbsentOnLinux()
    {
        using var session = ObsSession.StartWithSourceTypes();

        Assert.Null(ObsCaptureSource.GetGameCaptureProperties());
        Assert.Null(ObsCaptureSource.BuildGameCaptureSettings(new ObsGameCaptureTarget("title", null, "/path/to/game")));

        using var source = ObsSource.CreatePrivate(XshmInputId, "not a game source");
        Assert.False(ObsCaptureSource.Retarget(source, new ObsGameCaptureTarget("title", null, null)));
    }

    [SkippableFact]
    public void Retargeting_ADisplaySource_IsRefusedOnLinux()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var source = ObsSource.CreatePrivate(XshmInputId, "display");

        Assert.False(ObsCaptureSource.Retarget(source, new ObsGameCaptureTarget("some title", null, "game.exe")));
    }

    [SkippableFact]
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
