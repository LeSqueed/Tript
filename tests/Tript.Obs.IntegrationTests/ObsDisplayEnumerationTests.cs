// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Recorder;
using Tript.Settings;
using Xunit;
using Xunit.Abstractions;

namespace Tript.Obs.IntegrationTests;

public sealed class ObsDisplayEnumerationTests
{
    private const string ColourSourceId = "color_source";

    private readonly ITestOutputHelper _output;

    public ObsDisplayEnumerationTests(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    public void TheDisplayCaptureSource_DescribesItsMonitors()
    {
        using var session = ObsSession.StartWithSourceTypes();
        var displayId = ObsCaptureSource.FindDisplayCaptureId();
        Assert.NotNull(displayId);

        using var source = ObsSource.CreatePrivate(displayId, "monitor list");
        var displays = ObsCaptureSource.EnumerateDisplays(source);

        foreach (var display in displays)
            _output.WriteLine($"id={display.Id} name={display.Name} {display.Width}x{display.Height} primary={display.Primary}");

        Assert.Equal(displays.Count, displays.Select(display => display.Id).Distinct().Count());
        Assert.All(displays, display =>
        {
            Assert.NotEmpty(display.Id);
            Assert.NotEmpty(display.Name);
        });

        Assert.Equal(displays.Count == 0 ? 0 : 1, displays.Count(display => display.Primary));
    }

    [SkippableFact]
    public void TheSavedMonitor_SelectsById_AndAMissingOneFallsBack()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var source = ObsSource.CreatePrivate(ObsCaptureSource.FindDisplayCaptureId()!, "monitor choice");

        var displays = ObsCaptureSource.EnumerateDisplays(source);
        if (displays.Count == 0)
            throw new Xunit.SkipException(
                "This machine's display capture enumerates no monitors; there is nothing to select by id.");

        var chosen = displays[^1];
        Assert.Equal(chosen, ObsCaptureSource.ResolveDisplay(displays, chosen.Id).Selected);

        var missing = ObsCaptureSource.ResolveDisplay(displays, "tript-no-such-monitor");
        Assert.True(missing.RequestedMissing);
        Assert.NotNull(missing.Selected);
        Assert.True(missing.Selected!.Primary);

        using var settings = ObsCaptureSource.BuildDisplayCaptureSettings(source, chosen);
        source.Update(settings);
        using var readBack = source.GetSettings();
        var key = source.EnumerateProperties()
            .First(property => property.Type == ObsPropertyType.List && property.Items.Count > 0 &&
                               (property.Name is "screen" or "monitor_id")).Name;
        Assert.True(readBack.HasUserValue(key));
    }

    [SkippableFact]
    public void TheCaptureMethod_DecidesWhetherTheSceneHasADisplayLayer()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var colour = ObsSource.CreatePrivate(ColourSourceId, "policy colour");

        using (var display = new ObsRecorderSession(session.Runtime, colour, null,
                   new CapturePolicy(DisplayCaptureMethod.Display, null, TimeSpan.FromSeconds(10))))
        {
            Assert.True(display.HasDisplayFallback);
        }

        using var game = new ObsRecorderSession(session.Runtime, colour, null,
            new CapturePolicy(DisplayCaptureMethod.Game, null, TimeSpan.FromSeconds(10)));

        Assert.False(game.HasDisplayFallback);
        Assert.Null(game.SelectedDisplay);
    }
}
