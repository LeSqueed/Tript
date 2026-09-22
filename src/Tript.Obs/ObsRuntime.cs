// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs.Interop;

namespace Tript.Obs;

public sealed record ObsStartupOptions
{
    public string Locale { get; init; } = "en-US";

    public string? ModuleConfigPath { get; init; }

    public ObsNixPlatform? NixPlatform { get; init; }
    public nint NixPlatformDisplay { get; init; }
}

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

    internal static long Generation => Volatile.Read(ref _generation);

    private static int _releasesInFlight;

    internal static bool TryEnterRelease(long generation)
    {
        Interlocked.Increment(ref _releasesInFlight);

        if (Volatile.Read(ref _generation) == generation)
            return true;

        Interlocked.Decrement(ref _releasesInFlight);
        return false;
    }

    internal static void ExitRelease() => Interlocked.Decrement(ref _releasesInFlight);

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

    public static bool IsInitialized => ObsNative.obs_initialized();

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
            // Before the generation bump, so these still release their handles for real rather than
            // having the release skipped as belonging to a dead runtime.
            ObsFrameSubscription.DisposeAllLive();
            ObsSourceShare.DisposeAllLive();
            ObsVolumeMeter.DisposeAllLive();
            DisposeLiveScenes();

            Interlocked.Increment(ref _generation);
            DrainReleases();

            ObsNative.obs_shutdown();

            _current = null;

            FreeInternedStrings();

            FrameSourceRegistry.Reset();
        }
    }

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

    public void AddDataPath(string path)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(path);
        ObsNative.obs_add_data_path(path);
    }

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

    public void AddModulePath(string binaryPath, string dataPath)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(binaryPath);
        ArgumentException.ThrowIfNullOrEmpty(dataPath);
        ObsNative.obs_add_module_path(binaryPath, dataPath);
    }

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

    public void PostLoadModules()
    {
        ThrowIfDisposed();
        ObsNative.obs_post_load_modules();
    }

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

    public readonly record struct DisplayColour(
        int MonitorIndex, ObsSourceColorSpace ColorSpace, float SdrWhiteLevelNits);

    private const int MaxDxgiOutputs = 16;
    private const int DuplicatorFrameWaitAttempts = 12;
    private static readonly TimeSpan DuplicatorFrameWaitInterval = TimeSpan.FromMilliseconds(5);

    private static bool IsHdrColourSpace(ObsSourceColorSpace space) =>
        space is ObsSourceColorSpace.Extended709 or ObsSourceColorSpace.Scrgb709;

    public IReadOnlyList<DisplayColour> ProbeDisplays()
    {
        ThrowIfDisposed();

        if (!OperatingSystem.IsWindows())
            return [];

        var probes = new List<DisplayColour>();
        ObsNative.obs_enter_graphics();
        try
        {
            for (var index = 0; index < MaxDxgiOutputs; index++)
            {
                var duplicator = ObsNative.gs_duplicator_create(index);
                if (duplicator == nint.Zero)
                    break;

                try
                {
                    if (ReadDuplicatorColour(duplicator, index) is not { } colour)
                        continue;

                    probes.Add(colour);
                    if (IsHdrColourSpace(colour.ColorSpace))
                        break;
                }
                finally
                {
                    ObsNative.gs_duplicator_destroy(duplicator);
                }
            }
        }
        finally
        {
            ObsNative.obs_leave_graphics();
        }

        return probes;
    }

    private static DisplayColour? ReadDuplicatorColour(nint duplicator, int index)
    {
        for (var attempt = 0; attempt < DuplicatorFrameWaitAttempts; attempt++)
        {
            if (!ObsNative.gs_duplicator_update_frame(duplicator))
                return null;

            if (ObsNative.gs_duplicator_get_texture(duplicator) != nint.Zero)
            {
                return new DisplayColour(index,
                    (ObsSourceColorSpace)ObsNative.gs_duplicator_get_color_space(duplicator),
                    ObsNative.gs_duplicator_get_sdr_white_level(duplicator));
            }

            Thread.Sleep(DuplicatorFrameWaitInterval);
        }

        return null;
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

        if (!Enum.IsDefined((ObsVideoResetResult)code))
            throw new ObsException($"obs_reset_video returned {code}, which is not a documented video status.");

        var result = (ObsVideoResetResult)code;
        if (result == ObsVideoResetResult.Success)
            EnsureVideoLevels();

        return result;
    }

    private void EnsureVideoLevels()
    {
        if (ObsNative.obs_get_video_sdr_white_level() > 0f &&
            ObsNative.obs_get_video_hdr_nominal_peak_level() > 0f)
        {
            return;
        }

        ObsNative.obs_set_video_levels(DefaultSdrWhiteLevelNits, DefaultHdrNominalPeakLevelNits);
    }

    public const float DefaultSdrWhiteLevelNits = 300f;
    public const float DefaultHdrNominalPeakLevelNits = 1000f;

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

    public bool IsVideoActive
    {
        get
        {
            ThrowIfDisposed();
            return ObsNative.obs_video_active();
        }
    }

    public bool HasVideo
    {
        get
        {
            ThrowIfDisposed();
            var native = default(ObsVideoInfoNative);
            return ObsNative.obs_get_video_info(ref native);
        }
    }

    internal bool TryGetVideoHandle(out nint video)
    {
        video = HasVideo ? ObsNative.obs_get_video() : nint.Zero;
        return video != nint.Zero;
    }

    internal bool IsCurrentVideoHandle(nint video) =>
        TryGetVideoHandle(out var current) && current == video;

    internal bool TryGetAudioHandle(out nint audio)
    {
        audio = ObsNative.obs_get_audio();
        return audio != nint.Zero;
    }

    public ulong FrameIntervalNanoseconds
    {
        get
        {
            ThrowIfDisposed();
            return ObsNative.obs_get_frame_interval_ns();
        }
    }

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

    public bool HasAudio
    {
        get
        {
            ThrowIfDisposed();
            return ObsNative.obs_get_audio() != nint.Zero;
        }
    }

    public const uint MaxOutputChannels = 64;

    public void SetOutputSource(uint channel, ObsSource? source)
    {
        ThrowIfDisposed();
        ThrowIfChannelOutOfRange(channel);
        ObsNative.obs_set_output_source(channel, source?.Pointer ?? nint.Zero);
    }

    public void SetOutputSource(uint channel, ObsScene scene)
    {
        ThrowIfDisposed();
        ThrowIfChannelOutOfRange(channel);
        ArgumentNullException.ThrowIfNull(scene);
        ObsNative.obs_set_output_source(channel, scene.SourcePointer);
    }

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

        for (var i = scenes.Length - 1; i >= 0; i--)
            scenes[i].Dispose();
    }

    public bool WaitForDestroyQueue()
    {
        ThrowIfDisposed();
        return ObsNative.obs_wait_for_destroy_queue();
    }

    public static long LiveAllocationCount => ObsNative.bnum_allocs();

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
