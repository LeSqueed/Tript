// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tript.Obs.Interop;

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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0)
            return;

        var stopEvent = StopListeningForStop();
        var savedEvent = StopListeningForSaved();
        if (stopEvent?.IsCurrentCallback == true)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                stopEvent.WaitForCallbacks(Timeout.InfiniteTimeSpan);
                _handle.Dispose();
            });
            savedEvent?.WaitForCallbacks(Timeout.InfiniteTimeSpan);
            return;
        }

        stopEvent?.WaitForCallbacks(Timeout.InfiniteTimeSpan);
        savedEvent?.WaitForCallbacks(Timeout.InfiniteTimeSpan);
        _handle.Dispose();
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
        catch
        {
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
        catch
        {
        }
    }
}

public readonly record struct ObsOutputProperty(string Name, ObsPropertyType Type);

public sealed record ObsOutputStopEvent(ObsOutputStopCode Code, string? LastError);

internal sealed class ObsOutputStopSubscription
{
    private readonly ObsOutput _output;
    private readonly List<EventHandler<ObsOutputStopEvent>> _handlers = [];
    private readonly SynchronizationContext? _context;
    private readonly object _handlerGate = new();
    private readonly object _callbackGate = new();
    private readonly ManualResetEventSlim _stopped = new(false);
    private readonly ThreadLocal<int> _callbackDepth = new();
    private GCHandle _pinned;
    private bool _connected;
    private bool _disconnectRequested;
    private int _callbacksInFlight;

    internal ObsOutputStopSubscription(ObsOutput output)
    {
        _output = output;
        _context = SynchronizationContext.Current;
    }

    internal bool IsEmpty
    {
        get
        {
            lock (_handlerGate)
                return _handlers.Count == 0;
        }
    }

    internal bool IsCurrentCallback => _callbackDepth.Value > 0;

    internal void Connect()
    {
        lock (_callbackGate)
        {
            if (_connected)
                return;

            _disconnectRequested = false;
            _pinned = GCHandle.Alloc(this);
            unsafe
            {
                ObsNative.signal_handler_connect(
                    ObsNative.obs_output_get_signal_handler(_output.Pointer), "stop",
                    &ObsOutput.OnStop, GCHandle.ToIntPtr(_pinned));
            }

            _connected = true;
        }
    }

    internal void Disconnect()
    {
        lock (_callbackGate)
        {
            if (!_connected)
                return;

            unsafe
            {
                ObsNative.signal_handler_disconnect(
                    ObsNative.obs_output_get_signal_handler(_output.Pointer), "stop",
                    &ObsOutput.OnStop, GCHandle.ToIntPtr(_pinned));
            }

            _connected = false;
            _disconnectRequested = true;
            ReleasePinIfIdleLocked();
        }
    }

    internal void PrepareForStart() => _stopped.Reset();

    internal bool WaitForStop(TimeSpan timeout)
    {
        if (!_stopped.Wait(timeout))
            return false;

        return WaitForCallbacks(timeout);
    }

    internal bool WaitForCallbacks(TimeSpan timeout)
    {
        var deadline = timeout == Timeout.InfiniteTimeSpan
            ? long.MaxValue
            : Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        lock (_callbackGate)
        {
            var ownCallbacks = _callbackDepth.Value;
            while (_callbacksInFlight != 0)
            {
                if (_callbacksInFlight <= ownCallbacks)
                    break;

                var remaining = timeout == Timeout.InfiniteTimeSpan
                    ? Timeout.Infinite
                    : (int)Math.Clamp(deadline - Environment.TickCount64, 0, int.MaxValue);
                if (remaining == 0 || !Monitor.Wait(_callbackGate, remaining))
                    return false;
            }

            ReleasePinIfIdleLocked();
            return true;
        }
    }

    internal void Add(EventHandler<ObsOutputStopEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_handlerGate)
            _handlers.Add(handler);
    }

    internal void Remove(EventHandler<ObsOutputStopEvent> handler)
    {
        lock (_handlerGate)
            _handlers.Remove(handler);
    }

    internal unsafe void OnNativeStop(nint calldata)
    {
        if (!TryEnterCallback())
            return;

        try
        {
            _callbackDepth.Value++;

            long code = 0;
            ObsNative.calldata_get_data(calldata, "code", &code, sizeof(long));

            string? lastError = null;
            nint errorPointer;
            if (ObsNative.calldata_get_string(calldata, "last_error", &errorPointer))
                lastError = Utf8Marshal.ReadBorrowed(errorPointer);

            var stop = new ObsOutputStopEvent((ObsOutputStopCode)code, lastError);
            _stopped.Set();
            EventHandler<ObsOutputStopEvent>[] handlers;
            lock (_handlerGate)
                handlers = _handlers.ToArray();

            if (_context is null)
            {
                foreach (var handler in handlers)
                    handler(_output, stop);
            }
            else
            {
                foreach (var handler in handlers)
                {
                    var captured = handler;
                    _context.Post(_ => captured(_output, stop), null);
                }
            }
        }
        finally
        {
            _callbackDepth.Value--;
            ExitCallback();
        }
    }

    private bool TryEnterCallback()
    {
        lock (_callbackGate)
        {
            if (!_connected)
                return false;

            _callbacksInFlight++;
            return true;
        }
    }

    private void ExitCallback()
    {
        lock (_callbackGate)
        {
            _callbacksInFlight--;
            ReleasePinIfIdleLocked();
            if (_callbacksInFlight == 0)
                Monitor.PulseAll(_callbackGate);
        }
    }

    private void ReleasePinIfIdleLocked()
    {
        if (_disconnectRequested && _callbacksInFlight == 0 && _pinned.IsAllocated)
            _pinned.Free();
    }
}

internal sealed class ObsOutputSignalSubscription
{
    private readonly ObsOutput _output;
    private readonly string _signal;
    private readonly List<EventHandler> _handlers = [];
    private readonly SynchronizationContext? _context;
    private readonly object _gate = new();
    private GCHandle _pinned;
    private bool _connected;
    private int _callbacksInFlight;
    private readonly ManualResetEventSlim _callbacksDone = new(true);

    internal ObsOutputSignalSubscription(ObsOutput output, string signal)
    {
        _output = output;
        _signal = signal;
        _context = SynchronizationContext.Current;
    }

    internal bool IsEmpty
    {
        get
        {
            lock (_gate)
                return _handlers.Count == 0;
        }
    }

    internal void Add(EventHandler handler)
    {
        lock (_gate)
            _handlers.Add(handler);
    }

    internal void Remove(EventHandler handler)
    {
        lock (_gate)
            _handlers.Remove(handler);
    }

    internal void Connect()
    {
        lock (_gate)
        {
            if (_connected)
                return;

            _pinned = GCHandle.Alloc(this);
            unsafe
            {
                ObsNative.signal_handler_connect(ObsNative.obs_output_get_signal_handler(_output.Pointer), _signal,
                    &ObsOutput.OnSaved, GCHandle.ToIntPtr(_pinned));
            }
            _connected = true;
        }
    }

    internal void Disconnect()
    {
        lock (_gate)
        {
            if (!_connected)
                return;

            unsafe
            {
                ObsNative.signal_handler_disconnect(ObsNative.obs_output_get_signal_handler(_output.Pointer), _signal,
                    &ObsOutput.OnSaved, GCHandle.ToIntPtr(_pinned));
            }
            _connected = false;
            while (_callbacksInFlight != 0)
                Monitor.Wait(_gate);
            if (_pinned.IsAllocated)
                _pinned.Free();
        }
    }

    internal void OnNativeSignal()
    {
        EventHandler[] handlers;
        lock (_gate)
        {
            if (!_connected)
                return;
            _callbacksInFlight++;
            _callbacksDone.Reset();
            handlers = _handlers.ToArray();
        }

        try
        {
            foreach (var handler in handlers)
            {
                if (_context is null)
                    handler(_output, EventArgs.Empty);
                else
                {
                    var captured = handler;
                    lock (_gate)
                    {
                        _callbacksInFlight++;
                        _callbacksDone.Reset();
                    }
                    try
                    {
                        _context.Post(_ =>
                        {
                            try
                            {
                                captured(_output, EventArgs.Empty);
                            }
                            finally
                            {
                                CompletePostedCallback();
                            }
                        }, null);
                    }
                    catch
                    {
                        CompletePostedCallback();
                        throw;
                    }
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _callbacksInFlight--;
                if (_callbacksInFlight == 0)
                {
                    _callbacksDone.Set();
                    Monitor.PulseAll(_gate);
                }
            }
        }
    }

    private void CompletePostedCallback()
    {
        lock (_gate)
        {
            _callbacksInFlight--;
            if (_callbacksInFlight == 0)
            {
                _callbacksDone.Set();
                Monitor.PulseAll(_gate);
            }
        }
    }

    internal bool WaitForCallbacks(TimeSpan timeout) => _callbacksDone.Wait(timeout);
}
