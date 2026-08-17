// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Shell;
using Xunit;

namespace Tript.App.Tests;

// The desktop shell's Linux audio-sink preflight. WebKitGTK aborts its render process when GStreamer
// has no autoaudiosink, and the symptom is a window that appears frozen while the host is healthy,
// so the shell refuses to open the window and prints the package to install instead.
//
// The value of these tests is the *shape* of the decision rather than the answer on any one machine:
// a false "missing" would refuse to start a working install, which is worse than the freeze the
// check prevents. So every fact reaches the decision through an injected probe, and the fail-open
// paths (unknown answers, Windows) are asserted directly instead of being inferred from whatever
// GStreamer happens to be installed on the machine running the suite.
//
// No app host is started here, so this class stays out of the port-binding smoke collection.
public sealed class WebviewAudioSinkTests
{
    // A probe that finds nothing, and an environment with no GStreamer variables set: the starting
    // point for the interesting cases, where the filesystem cannot answer the question.
    private static string? NoEnvironment(string _) => null;

    private static bool NothingExists(string _) => false;

    [Fact]
    public void OnWindows_TheSinkIsPresentWithoutProbingAnything()
    {
        // Windows renders with WebView2; GStreamer is irrelevant there, so the check must not even
        // look — a stat storm or a process launch on the Windows startup path would be pure cost.
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
        // The cheap answer: libgstautodetect.so sitting in a plugin directory is proof enough, and
        // it keeps a process launch off the startup path of every healthy install.
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
        // The only way to fail: no plugin anywhere we know to look, and gst-inspect-1.0 — which
        // reads the registry this machine actually uses — positively reports no such element.
        Assert.False(WebviewAudioSink.IsPresent(
            isWindows: false,
            NoEnvironment,
            NothingExists,
            inspectElement: () => false));
    }

    [Fact]
    public void WhenNothingIsOnDiskAndGstInspectSaysYes_TheSinkIsPresent()
    {
        // An unfamiliar plugin layout (a vendored GStreamer, a distro path we do not carry) is not
        // an absence: gst-inspect-1.0 knows better than the path list does.
        Assert.True(WebviewAudioSink.IsPresent(
            isWindows: false,
            NoEnvironment,
            NothingExists,
            inspectElement: () => true));
    }

    [Fact]
    public void WhenTheAnswerIsUnknown_TheCheckFailsOpen()
    {
        // No plugin found and no usable answer from gst-inspect-1.0 (not installed, timed out,
        // refused to launch): "present", because being wrong here must never block a working
        // install. This is the single most important assertion in the file.
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

        // Arch/CachyOS and Fedora, plus Debian/Ubuntu's multiarch directory: the three layouts the
        // README's install instructions target.
        Assert.Contains("/usr/lib/gstreamer-1.0/" + WebviewAudioSink.PluginLibrary, candidates);
        Assert.Contains("/usr/lib64/gstreamer-1.0/" + WebviewAudioSink.PluginLibrary, candidates);
        Assert.Contains("/usr/lib/x86_64-linux-gnu/gstreamer-1.0/" + WebviewAudioSink.PluginLibrary,
            candidates);

        // Every candidate is a path to the plugin itself, not a directory: the probe is File.Exists.
        Assert.All(candidates,
            path => Assert.EndsWith("/" + WebviewAudioSink.PluginLibrary, path, StringComparison.Ordinal));
        Assert.Equal(candidates.Distinct().Count(), candidates.Count);
    }

    [Fact]
    public void TheCandidatePaths_HonourGStreamersPluginPathVariables()
    {
        // GST_PLUGIN_PATH is how a relocated GStreamer (a flatpak runtime, a self-built stack) is
        // found at all, it holds several ':'-separated directories, and it is searched before the
        // system layouts.
        var candidates = WebviewAudioSink.CandidatePluginLibraries(
            variable => variable == "GST_PLUGIN_PATH" ? "/opt/gst/plugins:/opt/extra " : null);

        Assert.Equal("/opt/gst/plugins/" + WebviewAudioSink.PluginLibrary, candidates[0]);
        Assert.Equal("/opt/extra/" + WebviewAudioSink.PluginLibrary, candidates[1]);
        Assert.Contains("/usr/lib/gstreamer-1.0/" + WebviewAudioSink.PluginLibrary, candidates);
    }

    [Fact]
    public void TheCandidatePaths_IgnoreEmptyVariables()
    {
        // An empty or whitespace-only variable must not turn into a bogus candidate (Path.Combine
        // with an empty directory yields the bare filename, which would probe the process's cwd).
        var candidates = WebviewAudioSink.CandidatePluginLibraries(_ => "  ");

        Assert.All(candidates, path => Assert.StartsWith("/", path, StringComparison.Ordinal));
        Assert.DoesNotContain(WebviewAudioSink.PluginLibrary, candidates);
    }

    [Fact]
    public void Inspect_WithNoSuchProgram_IsUnknown() =>
        // gst-inspect-1.0 missing from PATH is a normal state (a machine with no GStreamer CLI
        // tools), and it must read as "cannot tell", never as "the element is missing".
        Assert.Null(WebviewAudioSink.Inspect(
            "tript-no-such-gst-inspect", WebviewAudioSink.Element, TimeSpan.FromSeconds(1)));

    [Fact]
    public void Inspect_ThatOutstaysItsTimeout_IsKilledAndUnknown()
    {
        // The guard exists to remove a hang, so it must not be able to introduce one: a child that
        // does not answer in time is killed and the answer is "unknown" (startup continues).
        if (OperatingSystem.IsWindows())
            return; // No /bin/sleep to stand in for a wedged gst-inspect-1.0; the check is Linux-only anyway.

        var started = DateTime.UtcNow;
        var answer = WebviewAudioSink.Inspect("sleep", "30", TimeSpan.FromMilliseconds(300));
        var elapsed = DateTime.UtcNow - started;

        Assert.Null(answer);
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"the probe must not wait for the child: took {elapsed}");
    }
}
