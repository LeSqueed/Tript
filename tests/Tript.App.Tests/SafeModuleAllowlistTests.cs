// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.RegularExpressions;
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
            ["obs-x264", "obs-ffmpeg", "obs-outputs", "obs-nvenc", "obs-qsv11", "win-capture", "image-source", "win-wasapi"],
            Program.SafeModules(isWindows: true));

    [Fact]
    public void TheLinuxAllowlist_IsTheSystemModulesAndNothingElse() =>
        Assert.Equal(
            ["obs-x264", "obs-ffmpeg", "obs-outputs", "linux-capture", "image-source", "linux-pulseaudio"],
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

    // obs-outputs registers mp4_output, the Hybrid MP4 writer that keeps a recording playable after
    // a crash. Dropping it silently falls back to ffmpeg_muxer, which only finalizes on a clean stop.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EveryPlatform_LoadsTheModuleThatRegistersHybridMp4(bool isWindows) =>
        Assert.Contains("obs-outputs", Program.SafeModules(isWindows));

    // A module in SafeModules that the Makefile never copies is simply absent at runtime, and one the
    // Makefile copies but SafeModules omits is shipped and never loaded. Neither fails loudly, which
    // is why the two lists have to be pinned to each other rather than just documented as matching.
    [Fact]
    public void TheMakefileBundlesExactlyTheWindowsAllowlist()
    {
        var makefile = File.ReadAllText(FindRepoFile("Makefile"));
        var match = Regex.Match(makefile, @"^OBS_MODULES\s*:=\s*(.+)$", RegexOptions.Multiline);
        Assert.True(match.Success, "OBS_MODULES was not found in the Makefile");

        var bundled = match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(Program.SafeModules(isWindows: true), bundled);
    }

    private static string FindRepoFile(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, name);
            if (File.Exists(candidate) && File.Exists(Path.Combine(directory.FullName, "Tript.slnx")))
                return candidate;
        }

        throw new FileNotFoundException($"{name} was not found above the test binary.");
    }
}
