// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Shell;
using Xunit;
using Tript.TestSupport;

namespace Tript.App.Tests;

public sealed class WebviewAudioSinkTests
{
    private static string? NoEnvironment(string _) => null;

    private static bool NothingExists(string _) => false;

    [Fact]
    public void OnWindows_TheSinkIsPresentWithoutProbingAnything()
    {
        var probed = false;
        var inspected = false;

        var present = WebviewAudioSink.IsPresent(
            isWindows: true,
            environment: _ => { probed = true; return null; },
            fileExists: _ => { probed = true; return false; },
            inspectElement: () => { inspected = true; return false; });

        Assert.True(present);
        Assert.False(probed, "the Windows path must not touch GStreamer's plugin directories");
        Assert.False(inspected, "the Windows path must not launch gst-inspect-1.0");
    }

    [Fact]
    public void WhenThePluginLibraryExists_TheSinkIsPresentAndNoProcessIsLaunched()
    {
        var inspected = false;

        var present = WebviewAudioSink.IsPresent(
            isWindows: false,
            NoEnvironment,
            fileExists: path => path.EndsWith(WebviewAudioSink.PluginLibrary, StringComparison.Ordinal),
            inspectElement: () => { inspected = true; return false; });

        Assert.True(present);
        Assert.False(inspected, "a plugin found on disk must settle the question");
    }

    [Fact]
    public void WhenNothingIsOnDiskAndGstInspectSaysNo_TheSinkIsMissing()
    {
        Assert.False(WebviewAudioSink.IsPresent(
            isWindows: false,
            NoEnvironment,
            NothingExists,
            inspectElement: () => false));
    }

    [Fact]
    public void WhenNothingIsOnDiskAndGstInspectSaysYes_TheSinkIsPresent()
    {
        Assert.True(WebviewAudioSink.IsPresent(
            isWindows: false,
            NoEnvironment,
            NothingExists,
            inspectElement: () => true));
    }

    [Fact]
    public void WhenTheAnswerIsUnknown_TheCheckFailsOpen()
    {
        Assert.True(WebviewAudioSink.IsPresent(
            isWindows: false,
            NoEnvironment,
            NothingExists,
            inspectElement: () => null));
    }

    [Fact]
    public void TheCandidatePaths_CoverTheDistroLayoutsAndAllNameThePlugin()
    {
        var candidates = WebviewAudioSink.CandidatePluginLibraries(NoEnvironment);

        Assert.Contains("/usr/lib/gstreamer-1.0/" + WebviewAudioSink.PluginLibrary, candidates);
        Assert.Contains("/usr/lib64/gstreamer-1.0/" + WebviewAudioSink.PluginLibrary, candidates);
        Assert.Contains("/usr/lib/x86_64-linux-gnu/gstreamer-1.0/" + WebviewAudioSink.PluginLibrary,
            candidates);

        Assert.All(candidates,
            path => Assert.EndsWith("/" + WebviewAudioSink.PluginLibrary, path, StringComparison.Ordinal));
        Assert.Equal(candidates.Distinct().Count(), candidates.Count);
    }

    [Fact]
    public void TheCandidatePaths_HonourGStreamersPluginPathVariables()
    {
        var candidates = WebviewAudioSink.CandidatePluginLibraries(
            variable => variable == "GST_PLUGIN_PATH" ? "/opt/gst/plugins:/opt/extra " : null);

        Assert.Equal("/opt/gst/plugins/" + WebviewAudioSink.PluginLibrary, candidates[0]);
        Assert.Equal("/opt/extra/" + WebviewAudioSink.PluginLibrary, candidates[1]);
        Assert.Contains("/usr/lib/gstreamer-1.0/" + WebviewAudioSink.PluginLibrary, candidates);
    }

    [Fact]
    public void TheCandidatePaths_IgnoreEmptyVariables()
    {
        var candidates = WebviewAudioSink.CandidatePluginLibraries(_ => "  ");

        Assert.All(candidates, path => Assert.StartsWith("/", path, StringComparison.Ordinal));
        Assert.DoesNotContain(WebviewAudioSink.PluginLibrary, candidates);
    }

    [Fact]
    public void Inspect_WithNoSuchProgram_IsUnknown() =>

        Assert.Null(WebviewAudioSink.Inspect(
            "tript-no-such-gst-inspect", WebviewAudioSink.Element, TimeSpan.FromSeconds(1)));

    [LinuxFact]
    public void Inspect_ThatOutstaysItsTimeout_IsKilledAndUnknown()
    {
        var started = DateTime.UtcNow;
        var answer = WebviewAudioSink.Inspect("sleep", "30", TimeSpan.FromMilliseconds(300));
        var elapsed = DateTime.UtcNow - started;

        Assert.Null(answer);
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"the probe must not wait for the child: took {elapsed}");
    }
}
