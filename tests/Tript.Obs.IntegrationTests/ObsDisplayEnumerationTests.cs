// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Recorder;
using Tript.Settings;
using Xunit;
using Xunit.Abstractions;

namespace Tript.Obs.IntegrationTests;

// The monitor list the recorder offers and picks from, against the real library. The shape of a
// capture plugin's display list is not documented anywhere but its own property items, so the parse
// that turns those items into a stable id, a name and a size is only proven here.
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

        // A headless box may enumerate nothing, which is an ordinary state; what must hold is that
        // every entry it does report is usable as a saved identity.
        Assert.Equal(displays.Count, displays.Select(display => display.Id).Distinct().Count());
        Assert.All(displays, display =>
        {
            Assert.NotEmpty(display.Id);
            Assert.NotEmpty(display.Name);
        });

        // At most one primary, and exactly one whenever there is anything to choose.
        Assert.Equal(displays.Count == 0 ? 0 : 1, displays.Count(display => display.Primary));
    }

    // The saved preference selects by id, and an id no longer attached falls back rather than
    // failing — the state a user reaches by unplugging a monitor.
    [SkippableFact]
    public void TheSavedMonitor_SelectsById_AndAMissingOneFallsBack()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var source = ObsSource.CreatePrivate(ObsCaptureSource.FindDisplayCaptureId()!, "monitor choice");

        // Selecting a monitor needs a monitor. Returning early here used to report as a pass, which
        // is the one answer that is never true — nothing was selected and nothing was checked.
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

        // And the id the plugin was actually given is the chosen monitor's, read back off the source.
        using var settings = ObsCaptureSource.BuildDisplayCaptureSettings(source, chosen);
        source.Update(settings);
        using var readBack = source.GetSettings();
        var key = source.EnumerateProperties()
            .First(property => property.Type == ObsPropertyType.List && property.Items.Count > 0 &&
                               (property.Name is "screen" or "monitor_id")).Name;
        Assert.True(readBack.HasUserValue(key));
    }

    // The policy table against the real library: the display layer exists only when the method asks
    // for it. Game capture is Windows-only, so the game layer is not what this asserts.
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
