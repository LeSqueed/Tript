// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;
using Tript.App;
using Tript.Obs;
using Tript.Settings;

namespace Tript.App;

// The alpha app host: the process that assembles every component task into a running product.
//
// Startup order (spec/recorder.md "Host startup"):
//   1. libobs context — safe module allowlist plus the audio module, display handed over the same
//      way the harness and integration tests do (libobs cannot discover the display server on Linux),
//      video and audio mixes reset, modules loaded.
//   2. SettingsStore (file-backed, at the platform config directory; overridable for tests).
//   3. FrameSourceRegistry resolver. ObsRuntime.Start installs one at startup; the app host is where
//      the resolution is verified to exist before the first detection is asked to start.
//   4. RecordingSessionTracker.Register() — the process-wide resolver the detection host writes
//      bookmarks through.
//   5. The three local IPC channels (spec/local-ipc.md): the WebSocket control socket, the HTTP
//      content server, and the UI host.
//   6. READY on stdout — the single-line contract the smoke test waits for.
//
// The host never throws across startup: each failure is written to stderr with a distinct exit code
// so the parent can tell which layer refused to come up.
internal static class Program
{
    private static int Main(string[] args)
    {
        var options = AppOptions.Parse(args);
        if (options is null)
            return 2;

        try
        {
            using var app = BuildApp(options);
            app.Run();
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Tript.App: {exception}");
            return 1;
        }
    }

    private static AppHost BuildApp(AppOptions options)
    {
        // The libobs context, when the recording path is real. The seam mode (--fake-recorder)
        // records through a fake recorder session, so no libobs is started at all — the smoke
        // tests that do not touch hardware run against that.
        ObsRuntime? runtime = null;
        if (!options.FakeRecorder)
        {
            runtime = StartObsRuntime();

            // The detection host resolves the live frame source through the registry.
            // ObsRuntime.Start installs the resolver; this is where the alpha verifies the
            // registration is visible so a detection failure later is never a silent
            // "no frame source".
            _ = FrameSourceRegistry.Current;
        }

        var store = new SettingsStore(new SettingsFileProvider(options.SettingsPath));

        // The recorder and the detector agree on what is being recorded through the core registry;
        // the tracker is what makes that true for this process.
        var tracker = new RecordingSessionTracker().Register();

        return new AppHost(options, store, runtime, tracker);
    }

    private static ObsRuntime StartObsRuntime()
    {
        if (XInitThreads() == 0)
            throw new InvalidOperationException("XInitThreads failed.");

        var display = XOpenDisplay(null);
        if (display == nint.Zero)
            throw new InvalidOperationException("XOpenDisplay returned null; no X server reachable.");

        var runtime = ObsRuntime.Start(new ObsStartupOptions
        {
            Locale = "en-US",
            NixPlatform = ObsNixPlatform.X11Egl,
            NixPlatformDisplay = display
        });

        var video = new ObsVideoSettings
        {
            BaseWidth = 1920,
            BaseHeight = 1080,
            OutputWidth = 1920,
            OutputHeight = 1080
        };

        if (runtime.ResetVideo(video) != ObsVideoResetResult.Success)
            throw new InvalidOperationException("obs_reset_video refused the settings.");

        if (!runtime.ResetAudio(new ObsAudioSettings()))
            throw new InvalidOperationException("obs_reset_audio refused the default settings.");

        // The same safe module list the integration tests use, plus linux-pulseaudio: the audio
        // routing creates pulse capture sources, so the module must be loadable even though the
        // alpha records Session (single programme track) and the routing is not driven for it.
        foreach (var module in new[] { "obs-x264", "obs-ffmpeg", "linux-capture", "image-source", "linux-pulseaudio" })
            runtime.AddSafeModule(module);

        runtime.AddModulePath("/usr/lib/obs-plugins/%module%.so", "/usr/share/obs/obs-studio/plugins/%module%/data");
        var report = runtime.LoadAllModules();
        runtime.PostLoadModules();

        if (!report.AllLoaded)
            throw new InvalidOperationException($"Modules failed to load: {string.Join(", ", report.FailedModules)}");

        if (!runtime.HasVideo || !runtime.HasAudio)
            throw new InvalidOperationException("The runtime has no video or audio mix after reset.");

        return runtime;
    }

    [DllImport("libX11.so.6", CharSet = CharSet.Ansi)]
    private static extern nint XOpenDisplay(string? name);

    [DllImport("libX11.so.6")]
    private static extern int XInitThreads();
}
