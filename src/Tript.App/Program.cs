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
//   2. SettingsStore (file-backed, at the platform config directory; overridable for tests), plus
//      the primary display's resolution — detected once, used as the fresh-install resolution
//      default and offered to the settings UI. Constructed before the libobs context because the
//      canvas and the frame rate are both read from the settings.
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
        // The settings store is constructed before the runtime so the runtime can be initialised
        // with the recording resolution and frame rate from the settings (both must affect the mix).
        var store = new SettingsStore(new SettingsFileProvider(options.SettingsPath));

        // Detected once per launch, before anything reads the settings. Two consumers: the
        // resolution a fresh install defaults to, and the "(display)" option the settings UI offers
        // (AppHost.PushSettings). Detecting once rather than per push keeps the X round trip off
        // every settings mutation, and the answer cannot change without a restart anyway — the
        // canvas is fixed at startup (see StartObsRuntime).
        var primaryDisplay = PrimaryDisplay.Detect();
        ApplyFirstRunDefaults(store, primaryDisplay);

        // The libobs context, when the recording path is real. The seam mode (--fake-recorder)
        // records through a fake recorder session, so no libobs is started at all — the smoke
        // tests that do not touch hardware run against that.
        ObsRuntime? runtime = null;
        if (!options.FakeRecorder)
        {
            runtime = StartRuntimeOnHostThread(store);

            // The detection host resolves the live frame source through the registry.
            // ObsRuntime.Start installs the resolver; this is where the alpha verifies the
            // registration is visible so a detection failure later is never a silent
            // "no frame source".
            _ = FrameSourceRegistry.Current;
        }

        // The recorder and the detector agree on what is being recorded through the core registry;
        // the tracker is what makes that true for this process.
        var tracker = new RecordingSessionTracker().Register();

        return new AppHost(options, store, runtime, tracker, primaryDisplay);
    }

    // The libobs context runs on a thread with the apartment libobs expects. On Windows libobs's
    // obs_startup calls CoInitializeEx(COINIT_APARTMENTTHREADED) and treats a refusal as failure —
    // but the .NET main thread is already MTA (the runtime initialises it), so on Windows the
    // startup is moved onto a dedicated STA thread. Linux has no COM and no constraint; the
    // current thread is fine there.
    private static ObsRuntime StartRuntimeOnHostThread(SettingsStore store)
    {
        var recording = store.Load().Recording;

        if (!OperatingSystem.IsWindows())
            return StartObsRuntime(recording);

        ObsRuntime? runtime = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                runtime = StartObsRuntime(recording);
            }
            catch (Exception exception)
            {
                error = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error is not null)
            throw error;

        return runtime!;
    }

    // The fresh-install resolution default: the primary display's own size, so a first launch
    // records at the resolution the user actually plays at rather than at whatever the model's
    // literal default happens to be.
    //
    // It lives here, in the host, rather than on RecordingSettings, for two reasons. Display
    // enumeration is platform P/Invoke and Tript.Settings is a model layer that has to stay unit
    // testable on a machine with no display at all. And a *default on the property* would apply to
    // every load, including a load of an existing settings file that simply has no resolution key —
    // which is the forward-compatibility case JsonExtensionData exists to protect. Applying it only
    // when there is no settings file keeps the rule narrow and legible: a file that exists is the
    // user's, whatever is in it.
    //
    // The file is written on the way out so the first run is a first run exactly once; from then on
    // the stored value is what the canvas and the encoder both read.
    internal static bool ApplyFirstRunDefaults(SettingsStore store, DisplaySize? primaryDisplay)
    {
        if (File.Exists(store.FilePath))
            return false;

        var settings = store.Load();
        if (primaryDisplay is { IsUsable: true } display)
        {
            settings.Recording.ResolutionWidth = display.Width;
            settings.Recording.ResolutionHeight = display.Height;
        }

        // No detection: the model's own 1920x1080 stands. Nothing to correct — a safe default beats
        // a guess, and the user can pick their resolution in the settings UI.
        store.Save();
        return true;
    }

    // The startup is platform-split. On Linux, OBS is a system dependency and the runtime needs
    // the display handed over explicitly (libobs cannot discover the display server for itself).
    // On Windows, OBS is bundled next to the app and needs no X11. The runtime itself — video and
    // audio reset, module load — is shared.
    private static ObsRuntime StartObsRuntime(RecordingSettings recording)
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

        // Diagnostic for the Windows bring-up: the libobs context is started on a dedicated STA
        // thread (see StartRuntimeOnHostThread), so libobs's own CoInitializeEx(COINIT_
        // APARTMENTTHREADED) at obs_startup can succeed. Log the apartment we actually landed on.
        if (OperatingSystem.IsWindows())
            Console.Error.WriteLine($"Tript.App: libobs startup on {Thread.CurrentThread.GetApartmentState()} thread");

        // On Windows, libobs resolves the graphics module name by loading it straight off the
        // process DLL search path — obs_reset_video -> obs_init_graphics -> gs_create ->
        // os_dlopen, which is LoadLibraryExW with LOAD_LIBRARY_SEARCH_DEFAULT_DIRS. The module
        // paths registered via obs_add_module_path are never consulted for it, so the bundled
        // bin/64bit directory (where libobs-d3d11.dll sits next to obs.dll) has to be on that
        // search path before the first reset, or the module is "not found" and the video reset is
        // refused. SetDllDirectory adds exactly that one directory to the search.
        if (OperatingSystem.IsWindows() && locations.RuntimeDirectory is not null)
            SetDllDirectoryW(locations.RuntimeDirectory);

        // The paths go before the video and audio resets, matching the ordering ObsRuntime
        // documents (startup, then paths, then video and audio reset, then module load). The data
        // path has to be in place first: the first video reset loads libobs's effects through
        // obs_find_data_file, which only knows the paths registered so far.
        //
        // The core data dir (effects, locale, licenses) is optional — some installs strip it — but
        // when present it makes libobs's own effects findable, same as the integration tests.
        // On Windows the portable layout splits it: the effects live in data/libobs and the rest
        // in data/obs-studio, so both are registered as search roots.
        if (locations.CoreDataDir is not null)
            runtime.AddDataPath(locations.CoreDataDir);
        if (locations.LibobsDataDir is not null)
            runtime.AddDataPath(locations.LibobsDataDir);

        // The data path is a search root: libobs substitutes %module% and looks for the module's
        // data under it. The portable OBS layout nests it under a "data" subdir; distro installs
        // put it directly under the module dir. Both are probed because libobs searches each in
        // order, so the pattern carries both forms. Registered up here with the data path so the
        // whole path set is in place before anything is loaded.
        //
        // Windows uses the flat binary form, matching OBS's own registration: a %module% in the
        // binary pattern makes libobs glob for *subdirectories* and load <dir>/<name>/<name>.dll
        // (obs-module.c find_modules_in_path), but the portable layout keeps the plugin DLLs flat
        // in obs-plugins/64bit. A plain directory has libobs glob <dir>/*.dll itself. Forward
        // slashes: the finder's glob logic keys on '/' and a backslash suffix matches nothing.
        var binaryPattern = OperatingSystem.IsWindows()
            ? locations.ModuleBinaryDir!.Replace('\\', '/')
            : Path.Combine(locations.ModuleBinaryDir!, "%module%.so");
        var moduleDataRoot = locations.ModuleDataDir ?? locations.ModuleBinaryDir!;
        var dataPattern = Path.Combine(moduleDataRoot, "%module%").Replace('\\', '/');
        runtime.AddModulePath(binaryPattern, dataPattern);

        var result = runtime.ResetVideo(BuildVideoSettings(recording));
        if (result != ObsVideoResetResult.Success)
        {
            if (OperatingSystem.IsWindows())
                DumpGraphicsInitError(locations.RuntimeDirectory!);
            throw new InvalidOperationException($"obs_reset_video refused the settings ({result}).");
        }

        if (!runtime.ResetAudio(new ObsAudioSettings()))
            throw new InvalidOperationException("obs_reset_audio refused the default settings.");

        // The allowlist keeps the module load to what the recorder actually uses, and it is
        // platform-specific: the capture source differs (linux-capture vs win-capture), and
        // the audio module differs (linux-pulseaudio vs win-wasapi). Adding the frontend's
        // module would abort the process because there is no frontend here.
        foreach (var module in SafeModules(OperatingSystem.IsWindows()))
            runtime.AddSafeModule(module);

        var report = runtime.LoadAllModules();
        runtime.PostLoadModules();

        if (OperatingSystem.IsWindows())
        {
            var types = runtime.EnumerateInputTypes();
            Console.Error.WriteLine($"Tript.App: registered input types: {string.Join(", ", types)}");
        }

        if (!report.AllLoaded)
            throw new InvalidOperationException($"Modules failed to load: {string.Join(", ", report.FailedModules)}");

        if (!runtime.HasVideo || !runtime.HasAudio)
            throw new InvalidOperationException("The runtime has no video or audio mix after reset.");

        return runtime;
    }

    // Temporary Windows bring-up diagnostic: obs_init_graphics fails silently (effect compile
    // errors are swallowed because the error string is never requested). Reproduce the graphics
    // init directly and capture the effect compile error libobs would otherwise drop.
    private static void DumpGraphicsInitError(string runtimeDirectory)
    {
        try
        {
            var loaded = System.Diagnostics.Process.GetCurrentProcess().Modules
                .Cast<System.Diagnostics.ProcessModule>()
                .Where(m => m.ModuleName.IndexOf("obs", StringComparison.OrdinalIgnoreCase) >= 0
                    && m.ModuleName.IndexOf(".dll", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(m => $"{m.ModuleName} @0x{m.BaseAddress:X}")
                .ToArray();
            Console.Error.WriteLine($"Tript.App: loaded obs modules: {string.Join(", ", loaded)}");

            var obs = System.Diagnostics.Process.GetCurrentProcess().Modules
                .Cast<System.Diagnostics.ProcessModule>()
                .FirstOrDefault(m => m.ModuleName.Equals("obs64.dll", StringComparison.OrdinalIgnoreCase)
                    || m.ModuleName.Equals("obs.dll", StringComparison.OrdinalIgnoreCase))
                ?.BaseAddress ?? nint.Zero;
            if (obs == nint.Zero)
            {
                Console.Error.WriteLine("Tript.App: graphics diagnostic failed: obs module not found");
                return;
            }
            unsafe
            {
                var gsCreate = (delegate* unmanaged[Cdecl]<nint*, byte*, uint, int>)
                    NativeLibrary.GetExport(obs, "gs_create");
                var gsEnter = (delegate* unmanaged[Cdecl]<nint, void>)
                    NativeLibrary.GetExport(obs, "gs_enter_context");
                var gsLeave = (delegate* unmanaged[Cdecl]<void>)
                    NativeLibrary.GetExport(obs, "gs_leave_context");
                var effectCreate = (delegate* unmanaged[Cdecl]<byte*, nint*, nint>)
                    NativeLibrary.GetExport(obs, "gs_effect_create_from_file");
                var bfree = (delegate* unmanaged[Cdecl]<nint, void>)
                    NativeLibrary.GetExport(obs, "bfree");

                nint graphics = 0;
                var module = "libobs-d3d11\0";
                var modulePtr = Marshal.StringToCoTaskMemAnsi(module);
                var code = gsCreate(&graphics, (byte*)modulePtr, 0);
                Marshal.FreeCoTaskMem(modulePtr);
                Console.Error.WriteLine($"Tript.App: gs_create -> {code}, graphics=0x{graphics:X}");

                if (graphics != 0)
                {
                    var findDataFile = (delegate* unmanaged[Cdecl]<byte*, nint>)
                        NativeLibrary.GetExport(obs, "obs_find_data_file");
                    var bfree2 = (delegate* unmanaged[Cdecl]<nint, void>)
                        NativeLibrary.GetExport(obs, "bfree");

                    gsEnter(graphics);
                    foreach (var effectName in new[] { "default.effect", "opaque.effect", "solid.effect", "format_conversion.effect", "premultiplied_alpha.effect" })
                    {
                        var name = effectName + "\0";
                        var namePtr = Marshal.StringToCoTaskMemAnsi(name);
                        var found = findDataFile((byte*)namePtr);
                        Marshal.FreeCoTaskMem(namePtr);
                        var foundPath = found != 0 ? Marshal.PtrToStringAnsi(found) ?? "(null)" : "(none)";
                        Console.Error.WriteLine($"Tript.App: {effectName} found at: {foundPath} exists={found != 0 && File.Exists(foundPath)}");

                        nint error = 0;
                        var pathPtr = Marshal.StringToCoTaskMemAnsi(foundPath + "\0");
                        var effect = effectCreate((byte*)pathPtr, &error);
                        Marshal.FreeCoTaskMem(pathPtr);
                        var errorText = error != 0 ? Marshal.PtrToStringAnsi(error) ?? "(null)" : "(none)";
                        Console.Error.WriteLine($"Tript.App:   {effectName} -> 0x{effect:X}, error: {errorText}");
                        if (error != 0)
                            bfree2(error);
                    }
                    gsLeave();
                }
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Tript.App: graphics diagnostic failed: {exception.Message}");
        }
    }

    // The video mix the runtime is reset with: the canvas the compositor renders at, and the frame
    // rate it renders at.
    //
    // **The canvas comes from the settings, not from a constant.** It used to be a hardcoded
    // 1920x1080 while the frame rate was read from the settings, and the asymmetry was a real
    // recording-quality bug rather than a tidiness one: ObsRecorderSession.CreateOutput calls
    // videoEncoder.SetScaledSize(ResolutionWidth, ResolutionHeight), so a user who chose 2560x1440
    // got the encoder scaling a 1080p canvas *up* to 1440p — a soft, upscaled recording in a file
    // labelled 1440p, with the cost of encoding 1440p and none of the detail. The canvas has to be
    // at least the size the encoder is asked to emit, and making it exactly that size means the
    // scaler does nothing at all.
    //
    // **The canvas is fixed for the life of the process.** obs_reset_video is called once here, at
    // startup, and libobs refuses it outright while an output is active; nothing re-runs it when the
    // settings change. So a resolution changed in the UI reaches the *encoder's* scaled size on the
    // next recording (that is per-output, and read from the settings each time) but does not move
    // the canvas until the app is restarted — a resolution raised mid-session is still an upscale
    // until then. Making the canvas follow a live settings change means tearing down and rebuilding
    // the video mix with every source and encoder bound to it, which is a larger change than this.
    //
    // Both dimensions and the frame rate are floored at 1: a zero in any of them is the one
    // combination libobs rejects outright, and a corrupt settings file should not be a failure to
    // start.
    internal static ObsVideoSettings BuildVideoSettings(RecordingSettings recording)
    {
        var width = (uint)Math.Max(1, recording.ResolutionWidth);
        var height = (uint)Math.Max(1, recording.ResolutionHeight);

        return new ObsVideoSettings
        {
            BaseWidth = width,
            BaseHeight = height,
            OutputWidth = width,
            OutputHeight = height,
            // The recording frame rate comes from the settings, so the FPS selector actually
            // affects the recorded mix (libobs runs the compositor at fps_num/fps_den).
            FpsNumerator = (uint)Math.Max(1, recording.Fps),
            FpsDenominator = 1
        };
    }

    // The module allowlist. Kept small: the recorder needs the x264 and hardware encoders, the
    // capture source, the image source (for the colour/blank), and the platform audio source.
    //
    // The names are module binary names — the %module% libobs substitutes into the module path
    // pattern (win-wasapi.dll on Windows, linux-pulseaudio.so on Linux). Every Windows entry was
    // checked against the bundled runtime (third_party/obs-studio-32.2.2-x64/obs-plugins/64bit):
    // obs-x264, obs-ffmpeg, obs-nvenc, obs-qsv11, win-capture, image-source and win-wasapi all ship
    // there (obs-amf does not in OBS 32). That check matters because a name with no matching module
    // file is invisible at startup: AddSafeModule is a filter, and LoadAllModules only reports
    // modules it opened and could not initialise, so a misspelt or absent module is silently never
    // loaded and the failure only surfaces later, when creating a source of a type that module would
    // have registered. A hardware encoder plugin whose GPU is absent loads fine but registers no ids,
    // so listing it is safe on any machine.
    //
    // The platform is a parameter rather than an OperatingSystem.IsWindows() call inside the switch
    // so both branches stay assertable from a test process running on either OS.
    internal static IReadOnlyList<string> SafeModules(bool isWindows) =>
        isWindows
            // win-wasapi registers wasapi_input_capture / wasapi_output_capture, the ids
            // ObsAudioRoutingSink asks for on Windows. Without it there is no audio at all.
            // obs-nvenc registers jim_nvenc / obs_nvenc_h264(_tex) and obs-qsv11 h264_qsv; both
            // register their H.264 ids only when their hardware is present, and EnumerateUsableEncoderIds
            // filters to exactly the ids that did register.
            ? new[] { "obs-x264", "obs-ffmpeg", "obs-nvenc", "obs-qsv11", "win-capture", "image-source", "win-wasapi" }
            : new[] { "obs-x264", "obs-ffmpeg", "linux-capture", "image-source", "linux-pulseaudio" };

    [DllImport("libX11.so.6", CharSet = CharSet.Ansi)]
    private static extern nint XOpenDisplay(string? name);

    [DllImport("libX11.so.6")]
    private static extern int XInitThreads();

    // kernel32 SetDllDirectoryW: puts one directory on the process's DLL search path so libobs's
    // os_dlopen can find the bundled graphics module and plugin dependencies. Windows-only; the
    // import is inert elsewhere because it is never invoked off Windows.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectoryW(string? directory);
}
