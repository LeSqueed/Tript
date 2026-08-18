// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;
using Xunit;

namespace Tript.Recorder.Tests;

// How a monitor gets a stable identity and how a saved one is resolved. Both plugins describe their
// monitors in one human string and nothing else, so the parse of that string is the whole basis for
// the id, the name, the size and which monitor is primary.
public sealed class DisplayMonitorSelectionTests
{
    // win-capture's monitor_capture: a String list whose values are opaque device-interface ids and
    // whose item names are "<adapter name>: <w>x<h> @ <x>,<y>".
    private static IReadOnlyList<ObsSourceProperty> WindowsMonitorList() =>
    [
        new("monitor_id", ObsPropertyType.List,
        [
            new(@"\\?\DISPLAY#DEL41A8#5&2b1f9e4&0&UID4353", ObsComboFormat.String, @"\\.\DISPLAY2: 2560x1440 @ -2560,0"),
            new(@"\\?\DISPLAY#GSM5B08#5&2b1f9e4&0&UID4354", ObsComboFormat.String, @"\\.\DISPLAY1: 1920x1080 @ 0,0")
        ])
    ];

    // linux-capture's xshm_input: an Int list of screen indices, named "Screen <output> (<w>x<h> @ <x>,<y>)".
    private static IReadOnlyList<ObsSourceProperty> XshmScreenList() =>
    [
        new("screen", ObsPropertyType.List,
        [
            new(0L, ObsComboFormat.Int, "Screen DP-2 (1920x1080 @ 1920,0)"),
            new(1L, ObsComboFormat.Int, "Screen HDMI-2 (1920x1080 @ 0,0)")
        ])
    ];

    // The id is what the plugin matches on, never the position: a monitor unplugged ahead of yours
    // renumbers the list, and an index saved as an identity then captures somebody else's screen.
    [Fact]
    public void OnWindows_TheIdIsTheOpaqueDeviceIdAndTheNameLosesTheGeometry()
    {
        var displays = ObsCaptureSource.DescribeDisplays(WindowsMonitorList());

        Assert.Equal(2, displays.Count);
        Assert.Equal(@"\\?\DISPLAY#DEL41A8#5&2b1f9e4&0&UID4353", displays[0].Id);
        Assert.Equal(@"\\.\DISPLAY2", displays[0].Name);
        Assert.Equal(2560, displays[0].Width);
        Assert.Equal(1440, displays[0].Height);
        Assert.Equal(0, displays[0].Index);
    }

    [Fact]
    public void OnLinux_TheIdIsTheScreenIndexAsAString()
    {
        var displays = ObsCaptureSource.DescribeDisplays(XshmScreenList());

        Assert.Equal(["0", "1"], displays.Select(display => display.Id));
        Assert.Equal("Screen DP-2", displays[0].Name);
        Assert.Equal(1920, displays[0].Width);
        Assert.Equal(1080, displays[0].Height);
    }

    // The monitor at the virtual desktop's origin is the primary one, which is the only thing either
    // plugin's label says about it — and it is not the first item in the list.
    [Fact]
    public void ThePrimaryMonitor_IsTheOneAtTheDesktopOrigin()
    {
        Assert.Equal(@"\\.\DISPLAY1",
            ObsCaptureSource.DescribeDisplays(WindowsMonitorList()).Single(display => display.Primary).Name);

        Assert.Equal("Screen HDMI-2",
            ObsCaptureSource.DescribeDisplays(XshmScreenList()).Single(display => display.Primary).Name);
    }

    // A label with no geometry in it (a plugin that formats differently) still yields a usable
    // entry: the whole label becomes the name and the first monitor stands in as primary.
    [Fact]
    public void ALabelWithoutGeometry_StillDescribesAMonitor()
    {
        IReadOnlyList<ObsSourceProperty> properties =
        [
            new("monitor_id", ObsPropertyType.List,
            [
                new("device-a", ObsComboFormat.String, "Front left"),
                new("device-b", ObsComboFormat.String, null)
            ])
        ];

        var displays = ObsCaptureSource.DescribeDisplays(properties);

        Assert.Equal("Front left", displays[0].Name);
        Assert.Equal(0, displays[0].Width);
        Assert.True(displays[0].Primary);

        // A nameless item is still selectable, so it gets a name of its own rather than a blank one.
        Assert.Equal("Display 2", displays[1].Name);
    }

    [Fact]
    public void ASourceWithNoDisplaySelection_DescribesNoMonitors()
    {
        Assert.Empty(ObsCaptureSource.DescribeDisplays([]));
        Assert.Empty(ObsCaptureSource.DescribeDisplays([new("window", ObsPropertyType.Text, [])]));
    }

    // An item with no value at all cannot be selected, so it is not offered.
    [Fact]
    public void AnItemWithNoValue_IsSkipped()
    {
        IReadOnlyList<ObsSourceProperty> properties =
        [
            new("monitor_id", ObsPropertyType.List,
            [
                new(null, ObsComboFormat.String, "\\\\.\\DISPLAY1: 1920x1080 @ 0,0"),
                new("device-b", ObsComboFormat.String, "\\\\.\\DISPLAY2: 1920x1080 @ 1920,0")
            ])
        ];

        var display = Assert.Single(ObsCaptureSource.DescribeDisplays(properties));
        Assert.Equal("device-b", display.Id);
    }

    // ---- resolution ----

    [Fact]
    public void NoPreference_MeansThePrimaryMonitor()
    {
        var displays = ObsCaptureSource.DescribeDisplays(WindowsMonitorList());

        foreach (var preference in new[] { null, string.Empty })
        {
            var resolution = ObsCaptureSource.ResolveDisplay(displays, preference);
            Assert.False(resolution.RequestedMissing);
            Assert.True(resolution.Selected!.Primary);
        }
    }

    [Fact]
    public void ASavedIdThatIsAttached_IsTheOneSelected()
    {
        var displays = ObsCaptureSource.DescribeDisplays(WindowsMonitorList());

        var resolution = ObsCaptureSource.ResolveDisplay(displays, displays[0].Id);

        Assert.False(resolution.RequestedMissing);
        Assert.Equal(displays[0], resolution.Selected);
    }

    // The state a user reaches by unplugging a monitor: this session records the primary one and
    // says so. The preference is the caller's to keep — nothing here rewrites it.
    [Fact]
    public void ASavedIdThatIsNotAttached_FallsBackToThePrimaryAndReportsIt()
    {
        var displays = ObsCaptureSource.DescribeDisplays(WindowsMonitorList());

        var resolution = ObsCaptureSource.ResolveDisplay(displays, "\\\\?\\DISPLAY#UNPLUGGED");

        Assert.True(resolution.RequestedMissing);
        Assert.True(resolution.Selected!.Primary);
    }

    // Matching is byte-for-byte: the ids are opaque plugin handles, and a case-insensitive match
    // could select a different monitor.
    [Fact]
    public void IdMatchingIsOrdinal()
    {
        var displays = ObsCaptureSource.DescribeDisplays(WindowsMonitorList());

        Assert.True(ObsCaptureSource.ResolveDisplay(displays, displays[0].Id.ToUpperInvariant()).RequestedMissing);
    }

    // Enumerating nothing is an ordinary state — a headless machine, a plugin that would not answer
    // — and the recorder must proceed rather than fail.
    [Fact]
    public void NoMonitorsAtAll_ResolvesToNothingRatherThanThrowing()
    {
        var resolution = ObsCaptureSource.ResolveDisplay([], "anything");

        Assert.Null(resolution.Selected);
        Assert.True(resolution.RequestedMissing);
        Assert.Null(ObsCaptureSource.ResolveDisplay([], null).Selected);
    }

    [Fact]
    public void TheSelectionSurface_RejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => ObsCaptureSource.DescribeDisplays(null!));
        Assert.Throws<ArgumentNullException>(() => ObsCaptureSource.ResolveDisplay(null!, null));
    }
}
