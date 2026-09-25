// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;
#if TRIPT_TRAINING
using Tript.App.Training;
#endif
using Serilog;
using Serilog.Events;
using Tript.Detection;
using Tript.App;
using Tript.App.Content;
using Tript.App.Models;
using Tript.Obs;
using Tript.Settings;

namespace Tript.App;

internal static class Program
{
    private static readonly Lock ObsLogGate = new();
    private static IDisposable? _obsLogScope;

    private static int Main(string[] args)
    {
        var options = AppOptions.Parse(args);
        if (options is null)
            return 2;

        AppLog.InstallCrashHandlers();
        try
        {
            using var app = BuildApp(options);
            using var signals = TerminationSignals.Register(app.Ipc.RequestShutdown);
            app.Run();
            return 0;
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "Tript.App: the app host failed to start or run");
            return 1;
        }
        finally
        {
            AppLog.Shutdown();
        }
    }

    // libobs formats every diagnostic that matters for a recorder, including D3D11 device loss,
    // encoder open failures, NVENC session limits and capture hook errors, and hands it to one
    // process-wide handler. Without this they are formatted and dropped. Installed before
    // obs_startup so module loading and graphics init are captured too.
    private static void InstallObsLogBridge()
    {
        lock (ObsLogGate)
        {
            if (_obsLogScope is not null)
                return;

            try
            {
                _obsLogScope = ObsLog.Install(static (level, message) =>
                {
                    var serilogLevel = IsExpectedModuleSkip(message) ? LogEventLevel.Debug : level switch
                    {
                        ObsLogLevel.Error => LogEventLevel.Error,
                        ObsLogLevel.Warning => LogEventLevel.Warning,
                        ObsLogLevel.Info => LogEventLevel.Information,
                        _ => LogEventLevel.Debug,
                    };
                    Log.Write(serilogLevel, "libobs: {Message}", message.TrimEnd());
                });
            }
            catch (Exception exception) when (exception is InvalidOperationException or DllNotFoundException
                or EntryPointNotFoundException)
            {
                Log.Warning(exception, "Tript.App: the libobs log bridge could not be installed");
            }
        }
    }

    internal static AppHost BuildApp(AppOptions options)
    {
        AppLog.Configure(options.LogDirectory, options.VerboseLog);

        DeclareDpiAwareness();

        var store = new SettingsStore(new SettingsFileProvider(options.SettingsPath));

#if TRIPT_TRAINING
        ModelService.ConfigureModelRoots(TrainingPaths.InstalledModelsPath, GameModelPaths.ModelsRoot);
#else
        ModelService.ConfigureUserModelRoot(GameModelPaths.ModelsRoot);
#endif

        var primaryDisplay = PrimaryDisplay.Detect();
        ApplyFirstRunDefaults(store, primaryDisplay);

        ObsRuntime? runtime = null;
        if (!options.FakeRecorder)
        {
            runtime = StartRuntimeOnHostThread(store);

            _ = FrameSourceRegistry.Current;
        }

        var tracker = new RecordingSessionTracker().Register();

        return new AppHost(options, store, runtime, tracker, primaryDisplay,
            enableModelDelivery: !options.FakeRecorder,
            storageProbe: FixedStorageProbe.ForFakeRecorder(options.FakeRecorder, Environment.GetEnvironmentVariable));
    }

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

        settings.Audio.Tracks.Add(new AudioTrack
        {
            Name = "Mic + Desktop",
            Sources =
            {
                new AudioSource { Name = "Microphone", Kind = AudioSourceKind.Input },
                new AudioSource { Name = "Desktop Audio", Kind = AudioSourceKind.Output },
            },
        });

        store.Save();
        return true;
    }

    private static ObsRuntime StartObsRuntime(RecordingSettings recording)
    {
        var locations = ObsRuntimeLocator.Discover();
        if (!locations.Found)
            throw new InvalidOperationException(
                "No OBS runtime was found. On Linux, install obs-studio (libobs + the plugin modules); " +
                "on Windows the bundled OBS runtime is missing from the app directory.");

        if (locations.RuntimeDirectory is not null)
            ObsRuntime.SetRuntimeDirectory(locations.RuntimeDirectory);

        InstallObsLogBridge();

        if (MuxerHelper.EnsureNextToApp(locations.ModuleBinaryDir) is null)
            Log.Warning("Tript.App: obs-ffmpeg-mux is not next to {Directory} and could not be linked there; " +
                        "recordings will fail to start. Install obs-studio, or link its obs-ffmpeg-mux into that folder.",
                Path.GetDirectoryName(Environment.ProcessPath));

        var startup = OperatingSystem.IsWindows()
            ? new ObsStartupOptions { Locale = "en-US" }
            : ConnectNixDisplay(locations.ModuleBinaryDir!);

        var runtime = ObsRuntime.Start(startup);

        if (OperatingSystem.IsWindows())
            Log.Debug("Tript.App: libobs startup on {Apartment} thread", Thread.CurrentThread.GetApartmentState());

        if (OperatingSystem.IsWindows() && locations.RuntimeDirectory is not null)
            SetDllDirectoryW(locations.RuntimeDirectory);

        if (locations.CoreDataDir is not null)
            runtime.AddDataPath(locations.CoreDataDir);
        if (locations.LibobsDataDir is not null)
            runtime.AddDataPath(locations.LibobsDataDir);

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

        var isWindows = OperatingSystem.IsWindows();
        foreach (var module in SafeModules(isWindows, isWindows || NvidiaEncoderLibraryLoads()))
            runtime.AddSafeModule(module);

        var report = runtime.LoadAllModules();
        runtime.PostLoadModules();

        Log.Information("Tript.App: registered input types: {Types}",
            string.Join(", ", runtime.EnumerateInputTypes()));
        Log.Information("Tript.App: registered output types: {Types}",
            string.Join(", ", ObsOutput.EnumerateTypeIds()));

        var fatal = FatalModuleFailures(report.FailedModules, isWindows);
        var tolerated = report.FailedModules.Except(fatal).ToArray();
        if (tolerated.Length > 0)
            Log.Warning("Tript.App: optional modules failed to load and are skipped: {Modules}",
                string.Join(", ", tolerated));

        if (fatal.Count > 0)
            throw new InvalidOperationException($"Modules failed to load: {string.Join(", ", fatal)}");

        if (!runtime.HasVideo || !runtime.HasAudio)
            throw new InvalidOperationException("The runtime has no video or audio mix after reset.");

        return runtime;
    }

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
            Log.Error("Tript.App: graphics init failed; loaded obs modules: {Modules}", string.Join(", ", loaded));

            var obs = System.Diagnostics.Process.GetCurrentProcess().Modules
                .Cast<System.Diagnostics.ProcessModule>()
                .FirstOrDefault(m => m.ModuleName.Equals("obs64.dll", StringComparison.OrdinalIgnoreCase)
                    || m.ModuleName.Equals("obs.dll", StringComparison.OrdinalIgnoreCase))
                ?.BaseAddress ?? nint.Zero;
            if (obs == nint.Zero)
            {
                Log.Error("Tript.App: graphics diagnostic failed: obs module not found");
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
                var gsDestroy = (delegate* unmanaged[Cdecl]<nint, void>)
                    NativeLibrary.GetExport(obs, "gs_destroy");
                var effectDestroy = (delegate* unmanaged[Cdecl]<nint, void>)
                    NativeLibrary.GetExport(obs, "gs_effect_destroy");

                nint graphics = 0;
                var module = "libobs-d3d11\0";
                var modulePtr = Marshal.StringToCoTaskMemAnsi(module);
                var code = gsCreate(&graphics, (byte*)modulePtr, 0);
                Marshal.FreeCoTaskMem(modulePtr);
                Log.Error("Tript.App: gs_create -> {Code}, graphics=0x{Graphics:X}", code, graphics);

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
                        Log.Error("Tript.App: {Effect} found at: {Path} exists={Exists}", effectName, foundPath,
                            found != 0 && File.Exists(foundPath));

                        nint error = 0;
                        var pathPtr = Marshal.StringToCoTaskMemAnsi(foundPath + "\0");
                        var effect = effectCreate((byte*)pathPtr, &error);
                        Marshal.FreeCoTaskMem(pathPtr);
                        var errorText = error != 0 ? Marshal.PtrToStringAnsi(error) ?? "(null)" : "(none)";
                        Log.Error("Tript.App:   {Effect} -> 0x{Handle:X}, error: {Error}", effectName, effect, errorText);
                        if (error != 0)
                            bfree2(error);
                        if (effect != 0)
                            effectDestroy(effect);
                    }
                    gsLeave();

                    // This is a second D3D11 device created only to explain the failure. Without
                    // gs_destroy it stays alive for the rest of the process.
                    gsDestroy(graphics);
                }
            }
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Tript.App: graphics diagnostic failed");
        }
    }

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

            FpsNumerator = (uint)Math.Max(1, recording.Fps),
            FpsDenominator = 1
        };
    }

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
        }
    }

    private static readonly nint PerMonitorAwareV2 = -4;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(nint value);

    private static ObsStartupOptions ConnectNixDisplay(string moduleBinaryDir)
    {
        var pipeWirePresent = File.Exists(Path.Combine(moduleBinaryDir, "linux-pipewire.so"));
        var platform = NixPlatformSelector.FromEnvironment(pipeWirePresent);

        if (platform == ObsNixPlatform.Wayland)
        {
            var wayland = wl_display_connect(null);
            if (wayland != nint.Zero)
            {
                Log.Information("Tript.App: libobs runs on Wayland; the screen is captured through the desktop portal");
                return new ObsStartupOptions
                {
                    Locale = "en-US",
                    NixPlatform = ObsNixPlatform.Wayland,
                    NixPlatformDisplay = wayland
                };
            }

            Log.Warning("Tript.App: the Wayland compositor refused a connection; trying X11 instead");
        }

        if (XInitThreads() == 0)
            throw new InvalidOperationException("XInitThreads failed.");

        var display = XOpenDisplay(null);
        if (display == nint.Zero)
            throw new InvalidOperationException(
                "No display server is reachable: neither Wayland nor X11 accepted a connection.");

        Log.Information("Tript.App: libobs runs on X11 (pipewire plugin {PipeWire})",
            pipeWirePresent ? "present" : "absent");
        return new ObsStartupOptions
        {
            Locale = "en-US",
            NixPlatform = ObsNixPlatform.X11Egl,
            NixPlatformDisplay = display
        };
    }

    internal static bool IsExpectedModuleSkip(string message) =>
        message.StartsWith("Skipping module '", StringComparison.Ordinal)
        && message.Contains("not on safe list", StringComparison.Ordinal);

    internal static IReadOnlyList<string> FatalModuleFailures(IReadOnlyList<string> failedModules, bool isWindows)
    {
        var required = RequiredModules(isWindows);
        return failedModules.Where(module => required.Contains(module, StringComparer.Ordinal)).ToArray();
    }

    internal static IReadOnlyList<string> RequiredModules(bool isWindows) =>
        isWindows
            ? SafeModules(isWindows: true)
            : new[] { "obs-x264", "obs-ffmpeg", "obs-outputs", "image-source" };

    private static bool NvidiaEncoderLibraryLoads()
    {
        if (!NativeLibrary.TryLoad("libnvidia-encode.so.1", out var handle))
            return false;

        NativeLibrary.Free(handle);
        return true;
    }

    internal static IReadOnlyList<string> SafeModules(bool isWindows, bool nvidiaEncoderAvailable) =>
        SafeModules(isWindows).Where(module => nvidiaEncoderAvailable || module != "obs-nvenc").ToArray();

    internal static IReadOnlyList<string> SafeModules(bool isWindows) =>
        isWindows

            // obs-outputs is here for mp4_output, OBS's Hybrid MP4 writer, which is what keeps a
            // recording playable after a crash. It also carries the RTMP/FLV outputs, which Tript
            // never creates. Keep in step with OBS_MODULES in the Makefile.
            ? new[] { "obs-x264", "obs-ffmpeg", "obs-outputs", "obs-nvenc", "obs-qsv11", "win-capture", "image-source", "win-wasapi" }
            : new[]
            {
                "obs-x264", "obs-ffmpeg", "obs-outputs", "image-source", "linux-capture", "linux-pipewire",
                "linux-pulseaudio", "obs-nvenc", "obs-qsv11", "linux-vkcapture"
            };

    [DllImport("libX11.so.6", CharSet = CharSet.Ansi)]
    private static extern nint XOpenDisplay(string? name);

    [DllImport("libwayland-client.so.0", CharSet = CharSet.Ansi)]
    private static extern nint wl_display_connect(string? name);

    [DllImport("libX11.so.6")]
    private static extern int XInitThreads();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectoryW(string? directory);
}
