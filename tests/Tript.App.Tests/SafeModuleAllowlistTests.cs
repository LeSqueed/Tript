// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.App.Tests;

// The module allowlist the app host hands libobs. AddSafeModule is a filter, and LoadAllModules only
// reports modules it opened and failed to initialise, so a module that is missing from this list is
// silently never loaded: the source types it registers do not exist, and the failure only shows up
// when a source of one of those types is created at record time. That makes the list worth pinning
// per platform rather than only on the OS the tests happen to run on — SafeModules takes the platform
// as an argument for exactly that reason.
//
// No app host is started here, so this class stays out of the port-binding smoke collection.
public sealed class SafeModuleAllowlistTests
{
    [Fact]
    public void TheWindowsAllowlist_CarriesTheWasapiAudioModule() =>
        // win-wasapi registers wasapi_input_capture / wasapi_output_capture, which is what
        // ObsAudioRoutingSink asks for on Windows. Without it there is no audio on Windows at all.
        Assert.Contains("win-wasapi", Program.SafeModules(isWindows: true));

    [Fact]
    public void TheLinuxAllowlist_CarriesThePulseaudioModule() =>
        // The Linux counterpart: linux-pulseaudio registers the pulse_* capture types.
        Assert.Contains("linux-pulseaudio", Program.SafeModules(isWindows: false));

    [Fact]
    public void TheWindowsAllowlist_IsTheBundledModulesAndNothingElse() =>
        // Every name here exists in the bundled runtime's obs-plugins/64bit as <name>.dll; a name
        // that matches no file would load nothing and report nothing. obs-nvenc and obs-qsv11 ship
        // in OBS 32 (obs-amf does not), and each registers its H.264 ids only when its GPU is present.
        Assert.Equal(
            ["obs-x264", "obs-ffmpeg", "obs-nvenc", "obs-qsv11", "win-capture", "image-source", "win-wasapi"],
            Program.SafeModules(isWindows: true));

    [Fact]
    public void TheLinuxAllowlist_IsTheSystemModulesAndNothingElse() =>
        Assert.Equal(
            ["obs-x264", "obs-ffmpeg", "linux-capture", "image-source", "linux-pulseaudio"],
            Program.SafeModules(isWindows: false));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EveryPlatform_GetsTheEncodersTheImageSourceAndExactlyOneAudioModule(bool isWindows)
    {
        var modules = Program.SafeModules(isWindows);

        Assert.Contains("obs-x264", modules);
        Assert.Contains("obs-ffmpeg", modules);
        Assert.Contains("image-source", modules);

        // Exactly one audio module per platform, and never the other platform's.
        var audioModules = modules.Where(m => m is "win-wasapi" or "linux-pulseaudio").ToArray();
        Assert.Single(audioModules);
        Assert.Equal(isWindows ? "win-wasapi" : "linux-pulseaudio", audioModules[0]);

        // The frontend module would abort the process (there is no frontend in this host).
        Assert.DoesNotContain("frontend-tools", modules);
        Assert.DoesNotContain("obs-browser", modules);
    }
}
