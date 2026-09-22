// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tript.Obs.Interop;
using Tript.Core;

namespace Tript.Obs;

public sealed class ObsOutput : IDisposable
{
    private readonly ObsOutputHandle _handle;
    private readonly object _stopGate = new();
    private ObsOutputStopSubscription? _stopEvent;
    private readonly object _savedGate = new();
    private ObsOutputSignalSubscription? _savedEvent;
    private int _disposeRequested;

    private ObsOutput(nint pointer) => _handle = new ObsOutputHandle(pointer);

    internal static ObsOutput FromOwnedPointer(nint pointer)
    {
        if (pointer == nint.Zero)
            throw new ObsException("libobs returned a null output where one was expected.");

        return new ObsOutput(pointer);
    }

    internal nint Pointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed || _handle.IsInvalid, this);
            return _handle.DangerousGetHandle();
        }
    }

    public static ObsOutput Create(string id, string name, ObsSettings? settings = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ThrowIfUnregistered(id);

        var pointer = ObsNative.obs_output_create(id, name, settings?.Pointer ?? nint.Zero, nint.Zero);
        if (pointer == nint.Zero)
            throw new ObsException(
                $"obs_output_create returned null for '{id}'. It reports no reason; the log handler is where one would appear.");

        return new ObsOutput(pointer);
    }

    // Draining in-flight callbacks before releasing the output is what keeps libobs from calling into
    // a freed handle. The drain used to be unbounded, and with no SynchronizationContext the stop
    // handlers run synchronously inside the callback, so a handler that blocked, for example on a
    // lock the disposing thread already held, hung shutdown forever. On timeout the handle is leaked
    // on purpose: releasing it while a callback may still be running is a native use-after-free,
    // which is far worse than one output left behind at exit.
    private static readonly TimeSpan CallbackDrainTimeout = TimeSpan.FromSeconds(10);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0)
            return;

        var stopEvent = StopListeningForStop();
        var savedEvent = StopListeningForSaved();
        if (stopEvent?.IsCurrentCallback == true)
        {
            var savedDrained = savedEvent?.WaitForCallbacks(CallbackDrainTimeout) ?? true;
            ThreadPool.QueueUserWorkItem(_ =>
                ReleaseIfDrained(stopEvent.WaitForCallbacks(CallbackDrainTimeout) && savedDrained));
            return;
        }

        var stopDrained = stopEvent?.WaitForCallbacks(CallbackDrainTimeout) ?? true;
        var savedDrainedNow = savedEvent?.WaitForCallbacks(CallbackDrainTimeout) ?? true;
        ReleaseIfDrained(stopDrained && savedDrainedNow);
    }

    private void ReleaseIfDrained(bool drained)
    {
        if (drained)
        {
            _handle.Dispose();
            return;
        }

        Diagnostics.Report(DiagnosticLevel.Error,
            $"An OBS output callback did not finish within {CallbackDrainTimeout.TotalSeconds:0}s; "
            + "the output handle is being leaked rather than released while a callback may still use it");
    }

    public static bool IsTypeRegistered(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return ObsNative.obs_output_get_display_name(id) != nint.Zero;
    }

    public static string? GetTypeDisplayName(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return Utf8Marshal.ReadBorrowed(ObsNative.obs_output_get_display_name(id));
    }

    public static ObsOutputFlags GetTypeFlags(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return (ObsOutputFlags)ObsNative.obs_get_output_flags(id);
    }

    public static IReadOnlyList<string> EnumerateTypeIds()
    {
        var ids = new List<string>();
        for (nuint index = 0; ObsNative.obs_enum_output_types(index, out var id); index++)
        {
            var value = Utf8Marshal.ReadBorrowed(id);
            if (value is not null)
                ids.Add(value);
        }

        return ids;
    }

    public static ObsSettings? GetTypeDefaults(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return ObsSettings.FromOwnedPointerOrNull(ObsNative.obs_output_defaults(id));
    }

    public static IReadOnlyList<ObsOutputProperty> EnumerateTypeProperties(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return EnumerateProperties(ObsNative.obs_get_output_properties(id));
    }

    public string Id => Utf8Marshal.ReadBorrowed(ObsNative.obs_output_get_id(Pointer)) ?? string.Empty;

    public string Name => Utf8Marshal.ReadBorrowed(ObsNative.obs_output_get_name(Pointer)) ?? string.Empty;

    public ObsOutputFlags Flags => (ObsOutputFlags)ObsNative.obs_output_get_flags(Pointer);

    public ObsSettings GetSettings() => ObsSettings.FromOwnedPointer(ObsNative.obs_output_get_settings(Pointer));

    public void Update(ObsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObsNative.obs_output_update(Pointer, settings.Pointer);
    }

    public bool CallProcedure(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        unsafe
        {
            CalldataNative data = default;
            try
            {
                CalldataNative* pointer = &data;
                return ObsNative.proc_handler_call(ObsNative.obs_output_get_proc_handler(Pointer), name,
                    (nint)pointer);
            }
            finally
            {
                if (data.Stack != nint.Zero)
                    ObsNative.bfree(data.Stack);
            }
        }
    }

    public string? CallStringProcedure(string name, string resultName)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(resultName);
        unsafe
        {
            CalldataNative data = default;
            try
            {
                nint value = nint.Zero;
                CalldataNative* pointer = &data;
                if (!ObsNative.proc_handler_call(ObsNative.obs_output_get_proc_handler(Pointer), name,
                        (nint)pointer))
                    return null;
                if (!ObsNative.calldata_get_string((nint)pointer, resultName, &value))
                    return null;
                return Utf8Marshal.ReadBorrowed(value);
            }
            finally
            {
                if (data.Stack != nint.Zero)
                    ObsNative.bfree(data.Stack);
            }
        }
    }

    public void SetVideoEncoder(ObsEncoder encoder)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ObsNative.obs_output_set_video_encoder(Pointer, encoder.Pointer);
    }

    public void SetVideoEncoder(ObsEncoder encoder, nuint index)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ObsNative.obs_output_set_video_encoder2(Pointer, encoder.Pointer, index);
    }

    public void SetAudioEncoder(ObsEncoder encoder, nuint index)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ObsNative.obs_output_set_audio_encoder(Pointer, encoder.Pointer, index);
    }

    public ObsEncoder? GetAudioEncoder(nuint index) =>
        ObsEncoder.FromBorrowedPointerOrNull(ObsNative.obs_output_get_audio_encoder(Pointer, index));

    public void SetMedia(nint video, nint audio) => ObsNative.obs_output_set_media(Pointer, video, audio);

    public void SetPreferredSize(uint width, uint height) =>
        ObsNative.obs_output_set_preferred_size(Pointer, width, height);

    public void SetReconnectSettings(int retryCount, int retrySeconds) =>
        ObsNative.obs_output_set_reconnect_settings(Pointer, retryCount, retrySeconds);

    public void SetDelay(uint seconds, ObsOutputDelayFlags flags) =>
        ObsNative.obs_output_set_delay(Pointer, seconds, (uint)flags);

    public bool IsActive => ObsNative.obs_output_active(Pointer);

    public bool CanPause => ObsNative.obs_output_can_pause(Pointer);

    public bool IsPaused => ObsNative.obs_output_paused(Pointer);

    public bool Start()
    {
        lock (_stopGate)
            _stopEvent?.PrepareForStart();

        return ObsNative.obs_output_start(Pointer);
    }

    public void Stop() => ObsNative.obs_output_stop(Pointer);

    public void ForceStop() => ObsNative.obs_output_force_stop(Pointer);

    public bool SetPaused(bool paused) => ObsNative.obs_output_pause(Pointer, paused);

    public int FramesDropped => ObsNative.obs_output_get_frames_dropped(Pointer);

    public int TotalFrames => ObsNative.obs_output_get_total_frames(Pointer);

    public ulong TotalBytes => ObsNative.obs_output_get_total_bytes(Pointer);

    public float Congestion => ObsNative.obs_output_get_congestion(Pointer);

    public int ConnectTimeMilliseconds => ObsNative.obs_output_get_connect_time_ms(Pointer);

    public bool IsReconnecting => ObsNative.obs_output_reconnecting(Pointer);

    public string? LastError => Utf8Marshal.ReadBorrowed(ObsNative.obs_output_get_last_error(Pointer));

    public event EventHandler<ObsOutputStopEvent>? Stopped
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);

            lock (_stopGate)
            {
                if (_stopEvent is null)
                {
                    _stopEvent = new ObsOutputStopSubscription(this);
                }

                _stopEvent.Connect();
                _stopEvent.Add(value);
            }
        }
        remove
        {
            lock (_stopGate)
            {
                if (_stopEvent is null || value is null)
                    return;

                _stopEvent.Remove(value);
                if (_stopEvent.IsEmpty)
                {
                    _stopEvent.Disconnect();
                }
            }
        }
    }

    public event EventHandler? Saved
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_savedGate)
            {
                _savedEvent ??= new ObsOutputSignalSubscription(this, "saved");
                _savedEvent.Connect();
                _savedEvent.Add(value);
            }
        }
        remove
        {
            lock (_savedGate)
            {
                if (_savedEvent is null || value is null)
                    return;
                _savedEvent.Remove(value);
                if (_savedEvent.IsEmpty)
                    _savedEvent.Disconnect();
            }
        }
    }

    internal bool WaitForStop(TimeSpan timeout)
    {
        ObsOutputStopSubscription? stopEvent;
        lock (_stopGate)
            stopEvent = _stopEvent;

        return stopEvent?.WaitForStop(timeout) ?? true;
    }

    private ObsOutputStopSubscription? StopListeningForStop()
    {
        lock (_stopGate)
        {
            var stopEvent = _stopEvent;
            stopEvent?.Disconnect();
            _stopEvent = null;
            return stopEvent;
        }
    }

    private ObsOutputSignalSubscription? StopListeningForSaved()
    {
        lock (_savedGate)
        {
            var savedEvent = _savedEvent;
            savedEvent?.Disconnect();
            _savedEvent = null;
            return savedEvent;
        }
    }

    private static void ThrowIfUnregistered(string id)
    {
        if (ObsNative.obs_output_get_display_name(id) == nint.Zero)
            throw new ObsException(
                $"No loaded module registers an output type with the id '{id}'. " +
                "libobs would answer this with a placeholder output that writes nothing.");
    }

    private static IReadOnlyList<ObsOutputProperty> EnumerateProperties(nint propsPointer)
    {
        if (propsPointer == nint.Zero)
            return [];

        try
        {
            var properties = new List<ObsOutputProperty>();
            var property = ObsNative.obs_properties_first(propsPointer);

            while (property != nint.Zero)
            {
                properties.Add(new ObsOutputProperty(
                    Utf8Marshal.ReadBorrowed(ObsNative.obs_property_name(property)) ?? string.Empty,
                    (ObsPropertyType)ObsNative.obs_property_get_type(property)));

                if (!ObsNative.obs_property_next(ref property))
                    property = nint.Zero;
            }

            return properties;
        }
        finally
        {
            ObsNative.obs_properties_destroy(propsPointer);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void OnStop(nint parameter, nint calldata)
    {
        try
        {
            if (GCHandle.FromIntPtr(parameter).Target is ObsOutputStopSubscription stopEvent)
                stopEvent.OnNativeStop(calldata);
        }
        catch (Exception exception)
        {
            // Cannot propagate into libobs, but a failure here means the recording may never be
            // marked as stopped, which is the one thing that has to be traceable.
            Diagnostics.Report(DiagnosticLevel.Error,
                "libobs output stop callback failed; the recording may not finalize", exception);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void OnSaved(nint parameter, nint calldata)
    {
        try
        {
            if (GCHandle.FromIntPtr(parameter).Target is ObsOutputSignalSubscription signal)
                signal.OnNativeSignal();
        }
        catch (Exception exception)
        {
            Diagnostics.Report(DiagnosticLevel.Error,
                "libobs output saved callback failed; a replay or clip may not be reported as written", exception);
        }
    }
}

public readonly record struct ObsOutputProperty(string Name, ObsPropertyType Type);

public sealed record ObsOutputStopEvent(ObsOutputStopCode Code, string? LastError);
