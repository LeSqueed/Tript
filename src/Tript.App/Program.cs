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

    // The desktop shell (src/Tript.Shell) constructs its host through this same seam, so a
    // windowed build and the headless launcher are the same host rather than two divergent copies.
    internal static AppHost BuildApp(AppOptions options)
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

    // The startup is platform-split. On Linux, OBS is a system dependency and the runtime needs
    // the display handed over explicitly (libobs cannot discover the display server for itself).
    // On Windows, OBS is bundled next to the app and needs no X11. The runtime itself — video and
    // audio reset, module load — is shared.
    private static ObsRuntime StartObsRuntime()
    {
        var locations = ObsRuntimeLocator.Discover();
        if (!locations.Found)
            throw new InvalidOperationException(
                "No OBS runtime was found. On Linux, install obs-studio (libobs + the plugin modules); " +
                "on Windows the bundled OBS runtime is missing from the app directory.");

        if (locations.RuntimeDirectory is not null)
            ObsRuntime.SetRuntimeDirectory(locations.RuntimeDirectory);

        // The muxer helper must sit next to this executable (os_get_executable_path_ptr). On
        // Linux the system helper is symlinked beside us; on Windows the bundle already has it.
        // Best-effort — a machine without the helper will surface the failure at recording time.
        _ = MuxerHelper.EnsureNextToApp();

        ObsStartupOptions startup;
        if (OperatingSystem.IsWindows())
        {
            startup = new ObsStartupOptions { Locale = "en-US" };
        }
        else
        {
            if (XInitThreads() == 0)
                throw new InvalidOperationException("XInitThreads failed.");

            var display = XOpenDisplay(null);
            if (display == nint.Zero)
                throw new InvalidOperationException("XOpenDisplay returned null; no X server reachable.");

            startup = new ObsStartupOptions
            {
                Locale = "en-US",
                NixPlatform = ObsNixPlatform.X11Egl,
                NixPlatformDisplay = display
            };
        }

        var runtime = ObsRuntime.Start(startup);

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

        // The core data dir (effects, locale, licenses) is optional — some installs strip it — but
        // when present it makes libobs's own effects findable, same as the integration tests.
        if (locations.CoreDataDir is not null)
            runtime.AddDataPath(locations.CoreDataDir);

        // The allowlist keeps the module load to what the recorder actually uses, and it is
        // platform-specific: the capture source differs (linux-capture vs windows-capture), and
        // the audio module differs (linux-pulseaudio vs windows' wasapi). Adding the frontend's
        // module would abort the process because there is no frontend here.
        foreach (var module in SafeModules())
            runtime.AddSafeModule(module);

        // The data path is a search root: libobs substitutes %module% and looks for the module's
        // data under it. The portable OBS layout nests it under a "data" subdir; distro installs
        // put it directly under the module dir. Both are probed because libobs searches each in
        // order, so the pattern carries both forms.
        var binaryPattern = Path.Combine(locations.ModuleBinaryDir!, "%module%" + (OperatingSystem.IsWindows() ? ".dll" : ".so"));
        var moduleDataRoot = locations.ModuleDataDir ?? locations.ModuleBinaryDir!;
        var dataPattern = Path.Combine(moduleDataRoot, "%module%");
        runtime.AddModulePath(binaryPattern, dataPattern);
        var report = runtime.LoadAllModules();
        runtime.PostLoadModules();

        if (!report.AllLoaded)
            throw new InvalidOperationException($"Modules failed to load: {string.Join(", ", report.FailedModules)}");

        if (!runtime.HasVideo || !runtime.HasAudio)
            throw new InvalidOperationException("The runtime has no video or audio mix after reset.");

        return runtime;
    }

    // The module allowlist. Kept small: the recorder needs the x264/ffmpeg encoders, the capture
    // source, the image source (for the colour/blank), and the platform audio source.
    private static IReadOnlyList<string> SafeModules() =>
        OperatingSystem.IsWindows()
            ? new[] { "obs-x264", "obs-ffmpeg", "win-capture", "image-source" }
            : new[] { "obs-x264", "obs-ffmpeg", "linux-capture", "image-source", "linux-pulseaudio" };

    [DllImport("libX11.so.6", CharSet = CharSet.Ansi)]
    private static extern nint XOpenDisplay(string? name);

    [DllImport("libX11.so.6")]
    private static extern int XInitThreads();
}
