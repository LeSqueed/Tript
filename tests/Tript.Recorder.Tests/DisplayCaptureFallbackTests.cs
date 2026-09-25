// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;
using Xunit;

namespace Tript.Recorder.Tests;

public sealed class DisplayCaptureFallbackTests
{
    private static readonly string[] WindowsInputTypes =
        ["color_source", "monitor_capture", "window_capture", "game_capture", "wasapi_output_capture"];

    private static readonly string[] LinuxInputTypes =
        ["color_source", "xshm_input", "xcomposite_input", "pulse_output_capture"];

    [Fact]
    public void OnWindows_TheDisplayLayerIsWinCapturesMonitorCapture() =>
        Assert.Equal("monitor_capture", ObsCaptureSource.SelectDisplayCaptureId(
            WindowsInputTypes,
            ObsCaptureSource.DisplayCaptureIdPreference(isWindows: true, preferPortal: false)));

    [Fact]
    public void OnX11_TheDisplayLayerIsLinuxCapturesXshmInput() =>
        Assert.Equal("xshm_input", ObsCaptureSource.SelectDisplayCaptureId(
            LinuxInputTypes,
            ObsCaptureSource.DisplayCaptureIdPreference(isWindows: false, preferPortal: false)));

    [Fact]
    public void OnWayland_ThePortalSourceIsPreferredOverXshm()
    {
        string[] registered = ["xshm_input", "pipewire-desktop-capture-source"];

        Assert.Equal("pipewire-desktop-capture-source", ObsCaptureSource.SelectDisplayCaptureId(
            registered,
            ObsCaptureSource.DisplayCaptureIdPreference(isWindows: false, preferPortal: true)));

        Assert.Equal("xshm_input", ObsCaptureSource.SelectDisplayCaptureId(
            ["xshm_input"],
            ObsCaptureSource.DisplayCaptureIdPreference(isWindows: false, preferPortal: true)));
    }

    [Fact]
    public void OnWayland_TheScreenCaptureSourceIsPreferredOverTheObsoleteDesktopCaptureId()
    {
        string[] registered =
            ["xshm_input", "pipewire-desktop-capture-source", "pipewire-screen-capture-source", "pipewire-window-capture-source"];

        Assert.Equal("pipewire-screen-capture-source", ObsCaptureSource.SelectDisplayCaptureId(
            registered,
            ObsCaptureSource.DisplayCaptureIdPreference(isWindows: false, preferPortal: true)));
    }

    [Fact]
    public void OnX11_ThePortalScreenCaptureIsTheFallbackWhenXshmIsMissing() =>
        Assert.Equal("pipewire-screen-capture-source", ObsCaptureSource.SelectDisplayCaptureId(
            ["pipewire-screen-capture-source"],
            ObsCaptureSource.DisplayCaptureIdPreference(isWindows: false, preferPortal: false)));

    [Fact]
    public void ARuntimeWithNoDisplayCapture_ReportsNoIdRatherThanGuessing()
    {
        Assert.Null(ObsCaptureSource.SelectDisplayCaptureId(
            ["color_source", "game_capture"],
            ObsCaptureSource.DisplayCaptureIdPreference(isWindows: true, preferPortal: false)));

        Assert.Null(ObsCaptureSource.SelectDisplayCaptureId(
            [],
            ObsCaptureSource.DisplayCaptureIdPreference(isWindows: false, preferPortal: false)));
    }

    [Fact]
    public void ThePlatformPreferences_ShareNoIds()
    {
        var windows = ObsCaptureSource.DisplayCaptureIdPreference(isWindows: true, preferPortal: false);
        var linux = ObsCaptureSource.DisplayCaptureIdPreference(isWindows: false, preferPortal: false);

        Assert.NotEmpty(windows);
        Assert.NotEmpty(linux);
        Assert.Empty(windows.Intersect(linux));
    }

    [Fact]
    public void SelectionFollowsThePreferenceOrderRatherThanTheRegistrationOrder()
    {
        string[] registered = ["pipewire-desktop-capture-source", "xshm_input"];

        Assert.Equal("xshm_input", ObsCaptureSource.SelectDisplayCaptureId(registered, ["xshm_input", "pipewire-desktop-capture-source"]));
        Assert.Equal("pipewire-desktop-capture-source", ObsCaptureSource.SelectDisplayCaptureId(registered, ["pipewire-desktop-capture-source", "xshm_input"]));
    }

    [Fact]
    public void SelectDisplayCaptureId_RejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => ObsCaptureSource.SelectDisplayCaptureId(null!, ["xshm_input"]));
        Assert.Throws<ArgumentNullException>(() => ObsCaptureSource.SelectDisplayCaptureId(["xshm_input"], null!));
    }
}
