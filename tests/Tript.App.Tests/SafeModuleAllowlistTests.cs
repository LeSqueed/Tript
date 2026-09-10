// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.App.Tests;

public sealed class SafeModuleAllowlistTests
{
    [Fact]
    public void TheWindowsAllowlist_CarriesTheWasapiAudioModule() =>

        Assert.Contains("win-wasapi", Program.SafeModules(isWindows: true));

    [Fact]
    public void TheLinuxAllowlist_CarriesThePulseaudioModule() =>

        Assert.Contains("linux-pulseaudio", Program.SafeModules(isWindows: false));

    [Fact]
    public void TheWindowsAllowlist_IsTheBundledModulesAndNothingElse() =>

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

        var audioModules = modules.Where(m => m is "win-wasapi" or "linux-pulseaudio").ToArray();
        Assert.Single(audioModules);
        Assert.Equal(isWindows ? "win-wasapi" : "linux-pulseaudio", audioModules[0]);

        Assert.DoesNotContain("frontend-tools", modules);
        Assert.DoesNotContain("obs-browser", modules);
    }
}
