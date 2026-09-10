// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;
#if TRIPT_TRAINING
using Tript.App.Training;
#endif
using Tript.Detection;
using Tript.App;
using Tript.App.Models;
using Tript.Obs;
using Tript.Settings;

namespace Tript.App;

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

    internal static AppHost BuildApp(AppOptions options)
    {
        AppLog.Configure();

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
            enableModelDelivery: !options.FakeRecorder);
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

        if (OperatingSystem.IsWindows())
            Console.Error.WriteLine($"Tript.App: libobs startup on {Thread.CurrentThread.GetApartmentState()} thread");

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

    internal static IReadOnlyList<string> SafeModules(bool isWindows) =>
        isWindows

            ? new[] { "obs-x264", "obs-ffmpeg", "obs-nvenc", "obs-qsv11", "win-capture", "image-source", "win-wasapi" }
            : new[] { "obs-x264", "obs-ffmpeg", "linux-capture", "image-source", "linux-pulseaudio" };

    [DllImport("libX11.so.6", CharSet = CharSet.Ansi)]
    private static extern nint XOpenDisplay(string? name);

    [DllImport("libX11.so.6")]
    private static extern int XInitThreads();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectoryW(string? directory);
}
