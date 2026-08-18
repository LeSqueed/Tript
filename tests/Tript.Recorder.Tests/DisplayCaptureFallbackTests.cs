// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;
using Xunit;

namespace Tript.Recorder.Tests;

// The desktop layer the recording scene keeps under the game capture. Its only job is that a game
// capture which never attaches records the desktop instead of a black rectangle, so what matters is
// that an id is picked on every platform that has one and that a platform with none is a reported
// state rather than a crash.
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

    // xshm_input stays registered under Wayland but captures the Xwayland root rather than the
    // session, so the portal source wins where one is registered.
    [Fact]
    public void OnWayland_ThePortalSourceIsPreferredOverXshm()
    {
        string[] registered = ["xshm_input", "pipewire-desktop-capture-source"];

        Assert.Equal("pipewire-desktop-capture-source", ObsCaptureSource.SelectDisplayCaptureId(
            registered,
            ObsCaptureSource.DisplayCaptureIdPreference(isWindows: false, preferPortal: true)));

        // And it degrades to xshm when the portal source is not loaded.
        Assert.Equal("xshm_input", ObsCaptureSource.SelectDisplayCaptureId(
            ["xshm_input"],
            ObsCaptureSource.DisplayCaptureIdPreference(isWindows: false, preferPortal: true)));
    }

    // A runtime with no display capture at all is a documented degraded state: the scene keeps its
    // colour background and the recording is black until the game capture hooks.
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

    // The Windows and Linux preferences must not overlap: a Linux id picked on Windows would create
    // a source type win-capture never registered, which obs_source_create answers with a
    // placeholder that renders nothing — the same black frame this whole layer exists to prevent.
    [Fact]
    public void ThePlatformPreferences_ShareNoIds()
    {
        var windows = ObsCaptureSource.DisplayCaptureIdPreference(isWindows: true, preferPortal: false);
        var linux = ObsCaptureSource.DisplayCaptureIdPreference(isWindows: false, preferPortal: false);

        Assert.NotEmpty(windows);
        Assert.NotEmpty(linux);
        Assert.Empty(windows.Intersect(linux));
    }

    // The preference is a ranking, not a set: the same ids in the other order must pick differently.
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
