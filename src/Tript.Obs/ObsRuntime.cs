// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs.Interop;

namespace Tript.Obs;

public sealed record ObsStartupOptions
{
    // Passed to every module so it can pick a translation file. Not the process culture.
    public string Locale { get; init; } = "en-US";

    // Where modules keep their own configuration. libobs accepts null and several modules cope
    // with it, so it stays optional rather than being invented here.
    public string? ModuleConfigPath { get; init; }

    // Linux only, and required before startup: libobs cannot discover the display server for
    // itself. The display pointer is an Xlib Display* or a wl_display*, opened by the host — the
    // binding does not link against either.
    public ObsNixPlatform? NixPlatform { get; init; }
    public nint NixPlatformDisplay { get; init; }
}

// libobs is a process-global singleton with no re-entrancy: one context, one video mix, one audio
// mix, one set of loaded modules. This type is the enforcement of that rather than a wrapper around
// it — Start refuses a second context instead of letting two owners each believe they have one.
//
// Ordering, which the headers imply but never state: startup, then paths, then video and audio
// reset, then module load, then PostLoadModules. Video reset needs the graphics module nameable,
// and modules register source types that expect a video mix to exist.
public sealed class ObsRuntime : IDisposable
{
    private static readonly Lock Gate = new();
    private static ObsRuntime? _current;
    private static long _generation;

    private readonly Dictionary<string, nint> _internedStrings = new(StringComparer.Ordinal);
    private int _disposed;

    private ObsRuntime()
    {
    }

    // Incremented by every shutdown. Handles created against an earlier context compare against
    // this to know their pointer no longer refers to anything.
    internal static long Generation => Volatile.Read(ref _generation);

    public static ObsRuntime? Current
    {
        get
        {
            lock (Gate)
            {
                return _current;
            }
        }
    }

    // Reflects libobs rather than this class, so it stays honest if something outside the binding
    // has initialised the context.
    public static bool IsInitialized => ObsNative.obs_initialized();

    // Packed as (major << 24) | (minor << 16) | patch.
    public static Version Version
    {
        get
        {
            var packed = ObsNative.obs_get_version();
            return new Version((int)(packed >> 24), (int)((packed >> 16) & 0xFF), (int)(packed & 0xFFFF));
        }
    }

    public static string VersionString =>
        Utf8Marshal.ReadBorrowed(ObsNative.obs_get_version_string()) ?? string.Empty;

    // Points the loader at a bundled OBS runtime. Must be called before anything else on this type.
    public static void SetRuntimeDirectory(string? directory) => ObsLibrary.SetRuntimeDirectory(directory);

    public static ObsRuntime Start(ObsStartupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (Gate)
        {
            if (_current is not null)
                throw new InvalidOperationException(
                    "An OBS runtime is already running in this process. libobs holds one global context; dispose the existing runtime first.");

            ObsLibrary.EnsureLoaded();

            if (ObsNative.obs_initialized())
                throw new InvalidOperationException(
                    "libobs reports an already-initialised context that this process does not own.");

            if (options.NixPlatform is { } platform)
            {
                if (OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException(
                        "obs_set_nix_platform exists only on the Unix build of libobs.");

                ObsNative.obs_set_nix_platform((int)platform);
                ObsNative.obs_set_nix_platform_display(options.NixPlatformDisplay);
            }

            if (!ObsNative.obs_startup(options.Locale, options.ModuleConfigPath, nint.Zero))
                throw new ObsException("obs_startup failed. It reports no reason; the log handler is where one would appear.");

            var runtime = new ObsRuntime();
            _current = runtime;
            return runtime;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        lock (Gate)
        {
            ObsNative.obs_shutdown();

            // After the shutdown, so no handle can be created against a context that is already
            // gone and still read the old generation.
            Interlocked.Increment(ref _generation);
            _current = null;

            // Only now is libobs certain not to read the strings it kept pointers to.
            FreeInternedStrings();
        }
    }

    // The locale every module is asked to translate into. Setting it re-runs obs_module_set_locale
    // across the loaded modules, so it is not free.
    public string Locale
    {
        get
        {
            ThrowIfDisposed();
            return Utf8Marshal.ReadBorrowed(ObsNative.obs_get_locale()) ?? string.Empty;
        }
        set
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(value);
            ObsNative.obs_set_locale(value);
        }
    }

    // ---- paths ----

    // Deprecated in the headers and still exported, still the only way to make libobs's own effects
    // and locale files findable.
    public void AddDataPath(string path)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(path);
        ObsNative.obs_add_data_path(path);
    }

    // Matches on the exact string that was added, not on an equivalent path. Data paths also
    // outlive obs_shutdown, so anything added has to be removed by whoever added it.
    public bool RemoveDataPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return ObsNative.obs_remove_data_path(path);
    }

    public string? FindDataFile(string file)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(file);
        return Utf8Marshal.ReadOwned(ObsNative.obs_find_data_file(file));
    }

    // ---- modules ----

    // Both arguments may contain %module%, which libobs substitutes with each module's own name.
    public void AddModulePath(string binaryPath, string dataPath)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(binaryPath);
        ArgumentException.ThrowIfNullOrEmpty(dataPath);
        ObsNative.obs_add_module_path(binaryPath, dataPath);
    }

    // An allowlist: once anything is added, everything not named is skipped. Empty means load
    // everything, which on a machine with a full OBS install pulls in plugins that expect a
    // frontend and abort the process when they do not find one.
    public void AddSafeModule(string name)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObsNative.obs_add_safe_module(name);
    }

    public ObsModuleLoadReport LoadAllModules()
    {
        ThrowIfDisposed();

        var failureInfo = default(ObsModuleFailureInfoNative);
        ObsNative.obs_load_all_modules2(ref failureInfo);

        try
        {
            return new ObsModuleLoadReport(ReadFailedModules(failureInfo));
        }
        finally
        {
            ObsNative.obs_module_failure_info_free(ref failureInfo);
        }
    }

    // Required after loading, per the header: modules that registered a dependency on another
    // module's types are only resolved here.
    public void PostLoadModules()
    {
        ThrowIfDisposed();
        ObsNative.obs_post_load_modules();
    }

    // Loads the image and nothing else — the module's own obs_module_load runs at InitModule.
    public ObsModuleOpenResult OpenModule(string path, string dataPath, out ObsModule module)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(dataPath);

        var code = ObsNative.obs_open_module(out var pointer, path, dataPath);
        module = new ObsModule(pointer);

        if (!Enum.IsDefined((ObsModuleOpenResult)code))
            throw new ObsException($"obs_open_module returned {code}, which is not a documented module status.");

        return (ObsModuleOpenResult)code;
    }

    public bool InitModule(ObsModule module)
    {
        ThrowIfDisposed();

        if (!module.IsValid)
            throw new ArgumentException("The module handle is null.", nameof(module));

        return ObsNative.obs_init_module(module.Pointer);
    }

    public string? GetModuleName(ObsModule module)
    {
        ThrowIfDisposed();

        return module.IsValid
            ? Utf8Marshal.ReadBorrowed(ObsNative.obs_get_module_name(module.Pointer))
            : null;
    }

    // What the loaded modules actually registered. The cheapest honest answer to "did the plugin
    // load", which a module count cannot give: a module can load and register nothing.
    public IReadOnlyList<string> EnumerateInputTypes()
    {
        ThrowIfDisposed();

        var ids = new List<string>();
        for (nuint index = 0; ObsNative.obs_enum_input_types(index, out var id); index++)
        {
            var value = Utf8Marshal.ReadBorrowed(id);
            if (value is not null)
                ids.Add(value);
        }

        return ids;
    }

    // ---- video ----

    public ObsVideoResetResult ResetVideo(ObsVideoSettings settings)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(settings);

        var native = new ObsVideoInfoNative
        {
            GraphicsModule = InternUtf8(settings.GraphicsModule),
            FpsNumerator = settings.FpsNumerator,
            FpsDenominator = settings.FpsDenominator,
            BaseWidth = settings.BaseWidth,
            BaseHeight = settings.BaseHeight,
            OutputWidth = settings.OutputWidth,
            OutputHeight = settings.OutputHeight,
            OutputFormat = (int)settings.OutputFormat,
            Adapter = settings.Adapter,
            GpuConversion = settings.GpuConversion ? (byte)1 : (byte)0,
            ColorSpace = (int)settings.ColorSpace,
            Range = (int)settings.Range,
            ScaleType = (int)settings.ScaleType
        };

        var code = ObsNative.obs_reset_video(ref native);

        // An unmapped code means libobs grew a failure this binding does not know how to
        // describe. Returning Failed for it would report the wrong reason with full confidence.
        if (!Enum.IsDefined((ObsVideoResetResult)code))
            throw new ObsException($"obs_reset_video returned {code}, which is not a documented video status.");

        return (ObsVideoResetResult)code;
    }

    // The struct is zeroed before the call rather than left uninitialised: obs_get_video_info
    // returns false without touching it when there is no video, and handing back whatever was on
    // the stack would look exactly like data.
    public bool TryGetVideoInfo(out ObsVideoSettings? settings)
    {
        ThrowIfDisposed();

        var native = default(ObsVideoInfoNative);
        if (!ObsNative.obs_get_video_info(ref native))
        {
            settings = null;
            return false;
        }

        settings = new ObsVideoSettings
        {
            BaseWidth = native.BaseWidth,
            BaseHeight = native.BaseHeight,
            OutputWidth = native.OutputWidth,
            OutputHeight = native.OutputHeight,
            FpsNumerator = native.FpsNumerator,
            FpsDenominator = native.FpsDenominator,
            GraphicsModule = Utf8Marshal.ReadBorrowed(native.GraphicsModule) ?? ObsVideoSettings.DefaultGraphicsModule,
            OutputFormat = (ObsVideoFormat)native.OutputFormat,
            Adapter = native.Adapter,
            GpuConversion = native.GpuConversion != 0,
            ColorSpace = (ObsColorSpace)native.ColorSpace,
            Range = (ObsVideoRange)native.Range,
            ScaleType = (ObsScaleType)native.ScaleType
        };

        return true;
    }

    // True while an output is running, which is when video settings become unchangeable.
    public bool IsVideoActive
    {
        get
        {
            ThrowIfDisposed();
            return ObsNative.obs_video_active();
        }
    }

    // Deliberately not obs_get_video() != null: that call dereferences the video mix without
    // checking for it and segfaults outright when none has been reset — measured on 32.2.1, and
    // documented nowhere. obs_get_video_info is the probe that answers safely.
    public bool HasVideo
    {
        get
        {
            ThrowIfDisposed();
            var native = default(ObsVideoInfoNative);
            return ObsNative.obs_get_video_info(ref native);
        }
    }

    // The video_t* the raw-frame callbacks subscribe to. Gated on HasVideo for the reason above:
    // there is no safe way to ask libobs for this pointer before video exists.
    internal bool TryGetVideoHandle(out nint video)
    {
        video = HasVideo ? ObsNative.obs_get_video() : nint.Zero;
        return video != nint.Zero;
    }

    // The frame interval the compositor is actually running at, in nanoseconds. Derived from the
    // frame rate rather than stored, so it is the one place the fraction that was requested can be
    // checked against what the compositor made of it.
    public ulong FrameIntervalNanoseconds
    {
        get
        {
            ThrowIfDisposed();
            return ObsNative.obs_get_frame_interval_ns();
        }
    }

    // ---- audio ----

    // Note the asymmetry with video: libobs reports audio failure as a bare false, with no vocabulary
    // at all. There is nothing to map, and inventing a reason here would be a guess.
    public bool ResetAudio(ObsAudioSettings settings)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(settings);

        var native = new ObsAudioInfoNative
        {
            SamplesPerSecond = settings.SamplesPerSecond,
            Speakers = (int)settings.Speakers
        };

        return ObsNative.obs_reset_audio(ref native);
    }

    public bool TryGetAudioInfo(out ObsAudioSettings? settings)
    {
        ThrowIfDisposed();

        var native = default(ObsAudioInfoNative);
        if (!ObsNative.obs_get_audio_info(ref native))
        {
            settings = null;
            return false;
        }

        settings = new ObsAudioSettings
        {
            SamplesPerSecond = native.SamplesPerSecond,
            Speakers = (ObsSpeakerLayout)native.Speakers
        };

        return true;
    }

    // Safe to call before any reset, unlike its video counterpart: obs_get_audio checks and returns
    // null. The asymmetry is libobs's, not this binding's.
    public bool HasAudio
    {
        get
        {
            ThrowIfDisposed();
            return ObsNative.obs_get_audio() != nint.Zero;
        }
    }

    // ---- teardown helpers ----

    // Drains libobs's deferred destruction queue. The call to make before asserting that an object
    // is gone, because release only schedules the destroy.
    public bool WaitForDestroyQueue()
    {
        ThrowIfDisposed();
        return ObsNative.obs_wait_for_destroy_queue();
    }

    // Live bmem allocations. Exposed for leak assertions in tests; it counts libobs's own
    // allocations only, and its floor is whatever the process has legitimately retained.
    public static long LiveAllocationCount => ObsNative.bnum_allocs();

    // obs_reset_video keeps the obs_video_info struct it is handed, pointer and all — it does not
    // copy the graphics module name. Measured: free that buffer and obs_get_video_info hands the
    // freed pointer straight back, and libobs compares against it on the next reset. So the buffer
    // belongs to the context, not to the call, and is released only once the context is gone.
    private nint InternUtf8(string value)
    {
        lock (_internedStrings)
        {
            if (_internedStrings.TryGetValue(value, out var existing))
                return existing;

            var allocated = Utf8Marshal.Allocate(value);
            _internedStrings[value] = allocated;
            return allocated;
        }
    }

    private void FreeInternedStrings()
    {
        lock (_internedStrings)
        {
            foreach (var pointer in _internedStrings.Values)
                Utf8Marshal.Free(pointer);

            _internedStrings.Clear();
        }
    }

    private static List<string> ReadFailedModules(ObsModuleFailureInfoNative failureInfo)
    {
        var failed = new List<string>((int)failureInfo.Count);
        for (nuint i = 0; i < failureInfo.Count; i++)
        {
            var pointer = System.Runtime.InteropServices.Marshal.ReadIntPtr(failureInfo.FailedModules, (int)i * nint.Size);
            var name = Utf8Marshal.ReadBorrowed(pointer);
            if (name is not null)
                failed.Add(name);
        }

        return failed;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
