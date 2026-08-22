// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;
#if TRIPT_TRAINING
using Tript.App.Training;
#endif
using Tript.Detection;
using Tript.App;
using Tript.Obs;
using Tript.Settings;

namespace Tript.App;

// The app host: the process that assembles every component into a running product.
//
// Startup order:
//   1. SettingsStore plus the primary display's resolution — both read before the libobs context,
//      because the canvas and the frame rate come from the settings.
//   2. libobs context — safe module allowlist, the display handed over explicitly (libobs cannot
//      discover the display server on Linux), video and audio mixes reset, modules loaded.
//   3. FrameSourceRegistry resolver, verified to exist before detection is asked to start.
//   4. RecordingSessionTracker.Register() — the resolver the detection host writes bookmarks through.
//   5. The three local IPC channels: WebSocket control socket, HTTP content server, UI host.
//   6. READY on stdout.
//
// Startup never throws: each failure is written to stderr with a distinct exit code so the parent
// can tell which layer refused to come up.
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

    // The desktop shell constructs its host through this same seam, so a windowed build and the
    // headless launcher are the same host rather than two divergent copies.
    internal static AppHost BuildApp(AppOptions options)
    {
        // First, and in the shared seam rather than in Main, so the desktop shell gets the same
        // diagnostics the headless launcher does.
        AppLog.Configure();

        // Here for the same reason, and it has to be early: the shell hosts libobs in its own
        // process, and a DPI-unaware process cannot duplicate a desktop at all.
        DeclareDpiAwareness();

        // Before the runtime, so the runtime can be initialised with the resolution and frame rate from
        // the settings — both must affect the mix.
        var store = new SettingsStore(new SettingsFileProvider(options.SettingsPath));

#if TRIPT_TRAINING
        ModelService.ConfigureUserModelRoot(TrainingPaths.InstalledModelsPath);
#endif

        // Detected once per launch: the fresh-install resolution default, and the "(display)" option the
        // settings UI offers. The answer cannot change without a restart anyway — the canvas is fixed at
        // startup (see StartObsRuntime).
        var primaryDisplay = PrimaryDisplay.Detect();
        ApplyFirstRunDefaults(store, primaryDisplay);

        // Only when the recording path is real. --fake-recorder starts no libobs at all.
        ObsRuntime? runtime = null;
        if (!options.FakeRecorder)
        {
            runtime = StartRuntimeOnHostThread(store);

            // ObsRuntime.Start installs the resolver; this verifies the registration is visible, so a
            // detection failure later is never a silent "no frame source".
            _ = FrameSourceRegistry.Current;
        }

        // The recorder and the detector agree on what is being recorded through the core registry.
        var tracker = new RecordingSessionTracker().Register();

        return new AppHost(options, store, runtime, tracker, primaryDisplay);
    }

    // On Windows libobs's obs_startup calls CoInitializeEx(COINIT_APARTMENTTHREADED) and treats a
    // refusal as failure, but the .NET main thread is already MTA — so the startup is moved onto a
    // dedicated STA thread there. Linux has no COM and no constraint.
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
    // records at the resolution the user actually plays at. It lives in the host rather than on
    // RecordingSettings for two reasons.
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

        // No detection: the model's own 1920x1080 stands, and the user can pick their own in the UI.
        store.Save();
        return true;
    }

    // Platform-split. On Linux OBS is a system dependency and the runtime needs the display handed
    // over explicitly; on Windows OBS is bundled and needs no X11. The reset and module load are shared.
    private static ObsRuntime StartObsRuntime(RecordingSettings recording)
    {
        var locations = ObsRuntimeLocator.Discover();
        if (!locations.Found)
            throw new InvalidOperationException(
                "No OBS runtime was found. On Linux, install obs-studio (libobs + the plugin modules); " +
                "on Windows the bundled OBS runtime is missing from the app directory.");

        if (locations.RuntimeDirectory is not null)
            ObsRuntime.SetRuntimeDirectory(locations.RuntimeDirectory);

        // The muxer helper must sit next to this executable (os_get_executable_path_ptr). Best-effort —
        // a machine without it surfaces the failure at recording time.
        _ = MuxerHelper.EnsureNextToApp(locations.ModuleBinaryDir);

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

        // The libobs context is started on a dedicated STA thread (StartRuntimeOnHostThread); log the
        // apartment we actually landed on.
        if (OperatingSystem.IsWindows())
            Console.Error.WriteLine($"Tript.App: libobs startup on {Thread.CurrentThread.GetApartmentState()} thread");

        // libobs resolves the graphics module by loading it straight off the process DLL search
        // path (obs_reset_video -> obs_init_graphics -> gs_create -> os_dlopen, i.e. LoadLibraryExW
        // with LOAD_LIBRARY_SEARCH_DEFAULT_DIRS).
        if (OperatingSystem.IsWindows() && locations.RuntimeDirectory is not null)
            SetDllDirectoryW(locations.RuntimeDirectory);

        // Paths before the resets, matching the ordering ObsRuntime documents. The data path has to
        // be in place first: the first video reset loads libobs's effects through
        // obs_find_data_file, which only knows the paths registered so far.
        if (locations.CoreDataDir is not null)
            runtime.AddDataPath(locations.CoreDataDir);
        if (locations.LibobsDataDir is not null)
            runtime.AddDataPath(locations.LibobsDataDir);

        // A search root: libobs substitutes %module% and looks for the module's data under it.
        // Windows uses the FLAT binary form, matching OBS's own registration.
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

        // Platform-specific: the capture source differs (linux-capture vs win-capture) and so does the
        // audio module (linux-pulseaudio vs win-wasapi). Adding the frontend's module would abort the
        // process, because there is no frontend here.
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

    // Windows bring-up diagnostic: obs_init_graphics fails silently because the effect compile error
    // string is never requested. Reproduce the graphics init directly and capture that error.
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

    // The video mix the runtime is reset with: the canvas the compositor renders at, and its frame
    // rate. THE CANVAS COMES FROM THE SETTINGS, not a constant.
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
            // From the settings, so the FPS selector actually affects the recorded mix.
            FpsNumerator = (uint)Math.Max(1, recording.Fps),
            FpsDenominator = 1
        };
    }

    // Declares the process per-monitor DPI aware, before anything else runs.
    //
    // This is what makes display capture work at all on Windows. A DPI-unaware process gets
    // DXGI_ERROR_UNSUPPORTED from IDXGIOutput5::DuplicateOutput1 for EVERY output, so
    // gs_duplicator_create returns null, monitor capture renders nothing, and the recording is
    // whatever is underneath it. Microsoft's DuplicateOutput1 page does not document the
    // requirement — it was found by running the identical call in a DPI-aware process, where every
    // output duplicates, and a DPI-unaware one, where none do.
    //
    // Called first because the context can only be set before any window or DPI-dependent API in
    // the process, and a later call fails. The return is not checked: false means something already
    // set it, which is the outcome we wanted anyway.
    private static void DeclareDpiAwareness()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            SetProcessDpiAwarenessContext(PerMonitorAwareV2);
        }
        catch (EntryPointNotFoundException)
        {
            // Pre-1703 Windows. Nothing here can run on one, and display capture is already absent.
        }
    }

    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2.
    private static readonly nint PerMonitorAwareV2 = -4;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(nint value);

    // The module allowlist. Kept small: the x264 and hardware encoders, the capture source, the
    // image source (for the colour/blank), and the platform audio source.
    internal static IReadOnlyList<string> SafeModules(bool isWindows) =>
        isWindows
            // win-wasapi registers the wasapi_input_capture / wasapi_output_capture ids ObsAudioRoutingSink
            // asks for; without it there is no audio at all. obs-nvenc and obs-qsv11 register their H.264 ids
            // only when their hardware is present, and EnumerateUsableEncoderIds filters to what did register.
            ? new[] { "obs-x264", "obs-ffmpeg", "obs-nvenc", "obs-qsv11", "win-capture", "image-source", "win-wasapi" }
            : new[] { "obs-x264", "obs-ffmpeg", "linux-capture", "image-source", "linux-pulseaudio" };

    [DllImport("libX11.so.6", CharSet = CharSet.Ansi)]
    private static extern nint XOpenDisplay(string? name);

    [DllImport("libX11.so.6")]
    private static extern int XInitThreads();

    // Puts one directory on the process's DLL search path so libobs's os_dlopen can find the bundled
    // graphics module. Windows-only; inert elsewhere because it is never invoked off Windows.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectoryW(string? directory);
}
