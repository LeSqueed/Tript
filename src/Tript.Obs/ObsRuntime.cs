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

    // Releases currently inside a libobs release call. A handle checking its generation and then
    // calling release is two steps, so the stamp alone leaves a window where a shutdown lands
    // between them; shutdown drains this counter to close it. See TryEnterRelease.
    private static int _releasesInFlight;

    // Claims the right to release a pointer stamped with `generation`, or refuses because the
    // context that owned it is gone. Deliberately increments before reading the generation, and
    // Dispose increments the generation before reading this counter: with a full fence on each
    // side at least one of the two sees the other, so no release can slip past a shutdown.
    internal static bool TryEnterRelease(long generation)
    {
        Interlocked.Increment(ref _releasesInFlight);

        if (Volatile.Read(ref _generation) == generation)
            return true;

        Interlocked.Decrement(ref _releasesInFlight);
        return false;
    }

    internal static void ExitRelease() => Interlocked.Decrement(ref _releasesInFlight);

    // How long shutdown waits for in-flight releases. A release is a single libobs call, so this is
    // orders of magnitude more than it can legitimately need; it is bounded at all only because a
    // shutdown that can hang is worse than the leak that giving up produces.
    private static readonly TimeSpan ReleaseDrainTimeout = TimeSpan.FromSeconds(2);

    private static void DrainReleases()
    {
        var deadline = Environment.TickCount64 + (long)ReleaseDrainTimeout.TotalMilliseconds;
        var spin = new SpinWait();
        while (Volatile.Read(ref _releasesInFlight) != 0 && Environment.TickCount64 < deadline)
            spin.SpinOnce();
    }

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

            // The resolver is installed at Start rather than later so that a consumer reaching for
            // FrameSourceRegistry.Current always finds one. It resolves to the live runtime's own
            // frame source, and it is indirect because the pipeline is torn down and rebuilt
            // across a settings change — a captured instance would go stale silently.
            FrameSourceRegistry.SetResolver(() => new ObsFrameSource(runtime));

            return runtime;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        lock (Gate)
        {
            // Before the shutdown, and not merely as tidiness: a populated scene still referenced
            // when libobs shuts down is a segmentation fault, not a leak.
            DisposeLiveScenes();

            // Before the shutdown, and before draining: a handle whose release has not yet started
            // must observe the new generation and decline. Stamping afterwards would let a
            // finalizer read the old generation and release into memory obs_shutdown has freed.
            // A handle created in this window leaks instead, which obs_shutdown then reclaims.
            Interlocked.Increment(ref _generation);
            DrainReleases();

            ObsNative.obs_shutdown();

            _current = null;

            // Only now is libobs certain not to read the strings it kept pointers to.
            FreeInternedStrings();

            // The frame source the resolver handed out is backed by this runtime, which is now
            // gone; a later resolution would build an ObsFrameSource over a disposed runtime.
            // Clearing the registry here means a subsequent Start installs a fresh one.
            FrameSourceRegistry.Reset();
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

    // How many nits SDR white is taken to be, and the peak an HDR canvas is scaled to. These are the
    // numbers the compositor converts between the two with, and obs_reset_video does NOT set them —
    // OBS Studio's frontend does, from its own settings, so a bare libobs consumer that never calls
    // obs_set_video_levels is composing against whatever the process happened to start with. An SDR
    // source drawn onto a PQ canvas at an SDR white level of zero is black.
    public float SdrWhiteLevelNits
    {
        get
        {
            ThrowIfDisposed();
            return ObsNative.obs_get_video_sdr_white_level();
        }
    }

    public float HdrNominalPeakLevelNits
    {
        get
        {
            ThrowIfDisposed();
            return ObsNative.obs_get_video_hdr_nominal_peak_level();
        }
    }

    public void SetVideoLevels(float sdrWhiteLevelNits, float hdrNominalPeakLevelNits)
    {
        ThrowIfDisposed();
        ObsNative.obs_set_video_levels(sdrWhiteLevelNits, hdrNominalPeakLevelNits);
    }

    // What a monitor is actually displaying, straight from the desktop duplicator: its colour space
    // and the nits it treats as SDR white. This is the one Windows signal that answers "is this
    // display in HDR" honestly — the capture SOURCES do not, reporting Srgb for an HDR desktop and
    // for a hooked HDR game alike.
    //
    // It also has no ordering problem. A duplicator can be created and thrown away before any output
    // exists, so the canvas can be decided from it without the hook/render/output cycle that makes
    // the source route unusable.
    //
    // Null when the index names no monitor, or when duplication is refused — which on Windows is
    // most often because the process is not DPI aware, and is then null for every monitor.
    public (ObsSourceColorSpace ColorSpace, float SdrWhiteLevelNits)? ProbeDisplay(int monitorIndex)
    {
        ThrowIfDisposed();

        // Windows only, and not merely because that is where HDR desktops are: a duplicator is a
        // DXGI object, the OpenGL backend has no device function behind gs_duplicator_create, and
        // calling it there does not fail cleanly — it took the recording thread with it.
        if (!OperatingSystem.IsWindows())
            return null;

        ObsNative.obs_enter_graphics();
        try
        {
            var duplicator = ObsNative.gs_duplicator_create(monitorIndex);
            if (duplicator == nint.Zero)
                return null;

            try
            {
                for (var attempt = 0; attempt < 20; attempt++)
                {
                    if (!ObsNative.gs_duplicator_update_frame(duplicator))
                        return null;

                    if (ObsNative.gs_duplicator_get_texture(duplicator) != nint.Zero)
                        break;

                    Thread.Sleep(5);
                }

                if (ObsNative.gs_duplicator_get_texture(duplicator) == nint.Zero)
                    return null;

                return ((ObsSourceColorSpace)ObsNative.gs_duplicator_get_color_space(duplicator),
                    ObsNative.gs_duplicator_get_sdr_white_level(duplicator));
            }
            finally
            {
                ObsNative.gs_duplicator_destroy(duplicator);
            }
        }
        finally
        {
            ObsNative.obs_leave_graphics();
        }
    }

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

        var result = (ObsVideoResetResult)code;
        if (result == ObsVideoResetResult.Success)
            EnsureVideoLevels();

        return result;
    }

    // Zero is not a value the compositor can convert with, and zero is what a process that never
    // calls obs_set_video_levels has: obs_reset_video does not set these, and OBS Studio's frontend
    // is what normally does. At an SDR white level of zero every conversion between an SDR and an
    // HDR colour space collapses to black — an HDR game recorded onto a Rec.709 canvas is a black
    // file with working audio, and an SDR source on a PQ canvas is black the same way.
    //
    // Defaulted here rather than at the call sites so that no reset can leave the mix unable to
    // composite. An explicit SetVideoLevels afterwards still wins.
    private void EnsureVideoLevels()
    {
        if (ObsNative.obs_get_video_sdr_white_level() > 0f &&
            ObsNative.obs_get_video_hdr_nominal_peak_level() > 0f)
        {
            return;
        }

        ObsNative.obs_set_video_levels(DefaultSdrWhiteLevelNits, DefaultHdrNominalPeakLevelNits);
    }

    // OBS Studio's own defaults. 300 nits is the SDR white level Windows composites SDR content at
    // on a typical HDR desktop; a display's actual level can differ and is worth reading one day,
    // but any sane number beats zero by the whole difference between a picture and a black frame.
    public const float DefaultSdrWhiteLevelNits = 300f;
    public const float DefaultHdrNominalPeakLevelNits = 1000f;

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

    // Whether the mix with the given handle is the one that is current. A raw-frame subscription
    // that connected to a handle which a later obs_reset_video tore down must not disconnect from
    // it — that pointer is freed.
    internal bool IsCurrentVideoHandle(nint video) =>
        TryGetVideoHandle(out var current) && current == video;

    // The audio_t* encoders bind to. obs_get_audio checks and returns null before any reset — the
    // asymmetry with video is libobs's, measured — so this has the same shape as its video
    // counterpart purely for symmetry, not out of necessity.
    internal bool TryGetAudioHandle(out nint audio)
    {
        audio = ObsNative.obs_get_audio();
        return audio != nint.Zero;
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

    // ---- output channels ----

    // libobs composes what it outputs from 64 global channels, each holding at most one source —
    // usually a scene. This is how a scene becomes the thing being recorded. The header assigns no
    // meaning to any index; channel 0 for the programme scene is convention, not a rule.
    public const uint MaxOutputChannels = 64;

    // The channel takes a reference of its own, so the caller may dispose its source afterwards.
    // Passing null clears the channel and releases that reference.
    public void SetOutputSource(uint channel, ObsSource? source)
    {
        ThrowIfDisposed();
        ThrowIfChannelOutOfRange(channel);
        ObsNative.obs_set_output_source(channel, source?.Pointer ?? nint.Zero);
    }

    // The overload that matters in practice: a scene is a source, and reaching for its source
    // pointer at every call site is where a stray release eventually comes from.
    public void SetOutputSource(uint channel, ObsScene scene)
    {
        ThrowIfDisposed();
        ThrowIfChannelOutOfRange(channel);
        ArgumentNullException.ThrowIfNull(scene);
        ObsNative.obs_set_output_source(channel, scene.SourcePointer);
    }

    // Null when the channel is empty. The reference is incremented, so the result is the caller's to
    // dispose — reading a channel and forgetting that is a leak that keeps a whole scene alive.
    public ObsSource? GetOutputSource(uint channel)
    {
        ThrowIfDisposed();
        ThrowIfChannelOutOfRange(channel);
        return ObsSource.FromOwnedPointerOrNull(ObsNative.obs_get_output_source(channel));
    }

    private static void ThrowIfChannelOutOfRange(uint channel)
    {
        if (channel >= MaxOutputChannels)
            throw new ArgumentOutOfRangeException(nameof(channel), channel,
                $"libobs has {MaxOutputChannels} output channels; the index must be below that.");
    }

    // ---- scene registry ----
    //
    // Measured on 32.2.1 and stated nowhere: obs_shutdown segfaults if a scene the caller still
    // holds a reference to still has items attached. Leaked sources are freed cleanly and an empty
    // leaked scene is fine — it is specifically a populated, still-referenced scene that takes the
    // process down. Since that is a caller forgetting to dispose, and the punishment is a crash
    // rather than a leak, live scenes are tracked and disposed here before libobs is shut down.

    private static readonly Lock SceneGate = new();
    private static readonly List<ObsScene> LiveScenes = [];

    internal static void RegisterScene(ObsScene scene)
    {
        lock (SceneGate)
        {
            LiveScenes.Add(scene);
        }
    }

    internal static void UnregisterScene(ObsScene scene)
    {
        lock (SceneGate)
        {
            LiveScenes.Remove(scene);
        }
    }

    private static void DisposeLiveScenes()
    {
        ObsScene[] scenes;
        lock (SceneGate)
        {
            scenes = LiveScenes.ToArray();
            LiveScenes.Clear();
        }

        // Reverse order, so a scene added to another scene is taken apart before its container.
        for (var i = scenes.Length - 1; i >= 0; i--)
            scenes[i].Dispose();
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
    // freed pointer straight back, and libobs compares against it on the next reset.
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
