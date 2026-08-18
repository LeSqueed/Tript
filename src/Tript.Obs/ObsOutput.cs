// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tript.Obs.Interop;

namespace Tript.Obs;

// libobs's obs_output_t: the thing that turns a video mix, a set of encoders and a channel of sources
// into a file or a stream. A recorder owns one of these — ffmpeg_muxer for a session recording, and
// a replay-buffer output for the rolling save — and hands it the encoders and the settings that say
// where to write.
//
// Four measured properties of the native object shape this class, none of them stated in the
// headers:
//
//   * Creation copies the id and the name, and takes its own reference on the settings object, like
//     obs_source_create. Nothing here has to be kept alive.
//   * obs_output_create answers an *unregistered* id with a non-null placeholder — measured on
//     32.2.1 — so the binding rejects the id up front, the way ObsSource and ObsEncoder do, rather
//     than passing it through. The placeholder reports flags 0 and a null id of its own.
//   * obs_shutdown destroys every output regardless of outstanding references, so a handle that
//     outlives the context has nothing to release — hence ObsOutputHandle being context-owned.
//   * Failure is reported through three channels, and a binding must surface all three: the
//     synchronous obs_output_start return (false, with the reason in LastError if the output set
//     one), the asynchronous stop signal carrying the code and last_error, and the statistical
//     getters (frames dropped, total frames, total bytes). A recording that ends cleanly and one
//     that fails both arrive as a stop signal; the code is the only way to tell them apart.
//
// Not thread-safe as a wrapper. libobs guards the output's own state; a read-modify-write through
// this class is not atomic.
public sealed class ObsOutput : IDisposable
{
    private readonly ObsOutputHandle _handle;
    private readonly object _stopGate = new();
    private ObsOutputStopSubscription? _stopEvent;

    private ObsOutput(nint pointer) => _handle = new ObsOutputHandle(pointer);

    // For pointers libobs hands over already incremented — obs_get_output_by_name. The reference
    // becomes this object's to release.
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

    // ---- creation ----

    // Creates an output of a registered type. The settings object, if given, is *retained* rather
    // than copied: the output and the caller end up sharing it, and disposing the caller's
    // reference afterwards is safe only because libobs takes one of its own.
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
        if (_handle.IsClosed)
            return;

        StopListeningForStop();
        _handle.Dispose();
    }

    // ---- availability ----

    // Whether any loaded module registers the type. There is no obs_output_is_available; a plugin
    // that is not loaded registers nothing, so this — or the flag probe below — is the whole
    // vocabulary availability has.
    public static bool IsTypeRegistered(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return ObsNative.obs_output_get_display_name(id) != nint.Zero;
    }

    // The translated name of an output *type*, and the only reliable test of whether a type is
    // registered at all: null means no loaded module provides it. obs_output_create still answers a
    // non-null placeholder for an unregistered id — measured — so this probe is what a caller leans
    // on instead of null-checking the create result.
    public static string? GetTypeDisplayName(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return Utf8Marshal.ReadBorrowed(ObsNative.obs_output_get_display_name(id));
    }

    // The capability flags a type declares, read without constructing an instance. Whether an output
    // takes encoder packets (OBS_OUTPUT_ENCODED), requires a service (OBS_OUTPUT_SERVICE) or can be
    // paused (OBS_OUTPUT_CAN_PAUSE) is all decided here.
    public static ObsOutputFlags GetTypeFlags(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return (ObsOutputFlags)ObsNative.obs_get_output_flags(id);
    }

    // The ids every loaded module registered, in registration order.
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

    // The settings object a plugin falls back on for a type, so a caller builds its own settings by
    // starting here. Null when the id is not registered.
    public static ObsSettings? GetTypeDefaults(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return ObsSettings.FromOwnedPointerOrNull(ObsNative.obs_output_defaults(id));
    }

    // The properties a type exposes, free of the settings layer's round-trip assumptions: the keys
    // a plugin reads are the keys it declares here, so this is the authoritative account of what
    // can be configured. For ffmpeg_muxer on 32.2.1 that is exactly one property, path — measured —
    // and it is the whole key surface the plugin reads.
    public static IReadOnlyList<ObsOutputProperty> EnumerateTypeProperties(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return EnumerateProperties(ObsNative.obs_get_output_properties(id));
    }

    // ---- identity ----

    // The registered type id the output was created with, e.g. "ffmpeg_muxer".
    public string Id => Utf8Marshal.ReadBorrowed(ObsNative.obs_output_get_id(Pointer)) ?? string.Empty;

    public string Name => Utf8Marshal.ReadBorrowed(ObsNative.obs_output_get_name(Pointer)) ?? string.Empty;

    public ObsOutputFlags Flags => (ObsOutputFlags)ObsNative.obs_output_get_flags(Pointer);

    // ---- settings ----

    // The output's live settings object, with its reference incremented — the caller disposes it.
    // Editing it does not by itself reconfigure the output; Update is what tells the plugin to read
    // its settings again.
    public ObsSettings GetSettings() => ObsSettings.FromOwnedPointer(ObsNative.obs_output_get_settings(Pointer));

    // Applies the given keys over the output's existing settings.
    public void Update(ObsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObsNative.obs_output_update(Pointer, settings.Pointer);
    }

    // ---- wiring ----

    // Binds the video encoder the output will mux. Required for an encoded output; the encoder must
    // be bound to the video mix before the output starts, or start fails with "has no media set".
    public void SetVideoEncoder(ObsEncoder encoder)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ObsNative.obs_output_set_video_encoder(Pointer, encoder.Pointer);
    }

    // As SetVideoEncoder, for the video track at idx. Only used by outputs that declare multiple
    // video tracks; ffmpeg_muxer ignores the index.
    public void SetVideoEncoder(ObsEncoder encoder, nuint index)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ObsNative.obs_output_set_video_encoder2(Pointer, encoder.Pointer, index);
    }

    // Assigns the encoder to audio track slot idx — the third joint of the audio routing. The first
    // two are the source's mixer bitmask and the mixer index the encoder was created with; this is
    // the encoder-to-output-slot joint, and track n in the resulting file corresponds to slot n.
    public void SetAudioEncoder(ObsEncoder encoder, nuint index)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ObsNative.obs_output_set_audio_encoder(Pointer, encoder.Pointer, index);
    }

    // The audio encoder on slot index, or null when no encoder is assigned there. libobs hands back
    // the slot's own pointer without taking a reference, so this takes one: disposing what the raw
    // call returns would free an encoder the output still points at, and the process only dies for
    // it at obs_shutdown. The result is the caller's to dispose.
    public ObsEncoder? GetAudioEncoder(nuint index) =>
        ObsEncoder.FromBorrowedPointerOrNull(ObsNative.obs_output_get_audio_encoder(Pointer, index));

    // Binds the raw media feeds for a *non-encoded* output, which takes the mix directly rather than
    // encoder packets. Either feed may be null; an encoded output ignores both. Passing the handles
    // from ObsRuntime is the only way these are obtained.
    public void SetMedia(nint video, nint audio) => ObsNative.obs_output_set_media(Pointer, video, audio);

    // Requests a preferred scaled size for this output, applied to the encoder before start. 0,0
    // disables. The header warns it does nothing if the encoder is already active, so set it before
    // start.
    public void SetPreferredSize(uint width, uint height) =>
        ObsNative.obs_output_set_preferred_size(Pointer, width, height);

    // Configures reconnection for a streaming output. retryCount of 0 disables reconnection. A file
    // muxer never reconnects, which is what the default 0 says.
    public void SetReconnectSettings(int retryCount, int retrySeconds) =>
        ObsNative.obs_output_set_reconnect_settings(Pointer, retryCount, retrySeconds);

    // Applies an output delay. Takes effect at the next activation, not immediately, and the flags
    // decide whether the output keeps recording during the delay.
    public void SetDelay(uint seconds, ObsOutputDelayFlags flags) =>
        ObsNative.obs_output_set_delay(Pointer, seconds, (uint)flags);

    // ---- state ----

    // True while the output is running. For a file muxer, the window during which frames are being
    // written.
    public bool IsActive => ObsNative.obs_output_active(Pointer);

    // Whether the output can be paused; the OBS_OUTPUT_CAN_PAUSE flag, read per instance.
    public bool CanPause => ObsNative.obs_output_can_pause(Pointer);

    // Whether the output is currently paused.
    public bool IsPaused => ObsNative.obs_output_paused(Pointer);

    // Starts the output. The synchronous failure channel: false means the output refused to start,
    // and LastError carries the plugin's reason if it set one — measured: a bad recording path sets
    // one, a missing encoder does not.
    public bool Start() => ObsNative.obs_output_start(Pointer);

    // Asks the output to stop and waits for the muxed file to be finalised. The asynchronous stop
    // signal arrives with code OBS_OUTPUT_SUCCESS for a clean end. This call returns immediately;
    // the completion is the event.
    public void Stop() => ObsNative.obs_output_stop(Pointer);

    // Aborts an output that is waiting out a delay. "Usually only used with delay" is the header's
    // own description.
    public void ForceStop() => ObsNative.obs_output_force_stop(Pointer);

    // Pauses or resumes, for outputs with OBS_OUTPUT_CAN_PAUSE. Returns false when the output cannot
    // pause.
    public bool SetPaused(bool paused) => ObsNative.obs_output_pause(Pointer, paused);

    // ---- statistics ----

    // Frames dropped, total frames produced and total bytes written. The statistical failure channel;
    // for a file muxer, frames dropped is where a system too slow for the requested quality shows
    // up after the fact.
    public int FramesDropped => ObsNative.obs_output_get_frames_dropped(Pointer);

    public int TotalFrames => ObsNative.obs_output_get_total_frames(Pointer);

    public ulong TotalBytes => ObsNative.obs_output_get_total_bytes(Pointer);

    // Congestion (0..1) and connection time, meaningful for network outputs; a file muxer reports
    // zero congestion and zero connect time.
    public float Congestion => ObsNative.obs_output_get_congestion(Pointer);

    public int ConnectTimeMilliseconds => ObsNative.obs_output_get_connect_time_ms(Pointer);

    // True while a streaming output is attempting a reconnect.
    public bool IsReconnecting => ObsNative.obs_output_reconnecting(Pointer);

    // ---- failure surface ----

    // The plugin's own failure report, borrowed and possibly null. This is the companion to the
    // synchronous Start return: a false Start with a non-null LastError names the reason.
    public string? LastError => Utf8Marshal.ReadBorrowed(ObsNative.obs_output_get_last_error(Pointer));

    // The stop signal, exposed as an event. This is the asynchronous failure channel and the only
    // way to learn that a recording ended at all, whether cleanly (ObsOutputStopCode.Success) or
    // with a failure.
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
                    _stopEvent.Connect();
                }

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
                    _stopEvent = null;
                }
            }
        }
    }

    private void StopListeningForStop()
    {
        lock (_stopGate)
        {
            _stopEvent?.Disconnect();
            _stopEvent = null;
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

                // Releases the current item and overwrites it with the next, so there is exactly one
                // live item at a time and nothing to release once it returns false.
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

    // The native stop-signal callback. The signature is libobs's signal_callback_t: (param,
    // calldata).
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
            // Nothing here may throw across the native frame; a handler that throws terminates the
            // process. A failed dispatch shows up as a missing event, which the caller sees.
        }
    }
}

// One property as enumeration sees it. Outputs carry far fewer properties than encoders — ffmpeg_muxer
// declares exactly one, path — so the items a List property would carry are not needed here yet.
public readonly record struct ObsOutputProperty(string Name, ObsPropertyType Type);

// The payload of the stop signal: the stop code and the plugin's error text, taken from the calldata
// at the time the signal fired. Success is a valid code — a user-requested stop is how a recording
// ends well.
public sealed record ObsOutputStopEvent(ObsOutputStopCode Code, string? LastError);

// The native-side bookkeeping for the stop event. Holds the unmanaged function pointer, the
// GCHandle that keeps the managed side alive across the callback, the SynchronizationContext to
// marshal onto, and the managed subscribers.
internal sealed class ObsOutputStopSubscription
{
    private readonly ObsOutput _output;
    private readonly List<EventHandler<ObsOutputStopEvent>> _handlers = [];
    private readonly SynchronizationContext? _context;
    private GCHandle _pinned;
    private bool _connected;

    internal ObsOutputStopSubscription(ObsOutput output)
    {
        _output = output;
        _context = SynchronizationContext.Current;
    }

    internal bool IsEmpty => _handlers.Count == 0;

    // Registers the native callback on the output's signal handler. The GCHandle pins this object for
    // the whole connection, and the callback pointer is a static method with this object as its
    // parameter, so nothing can be collected out from under libobs.
    internal void Connect()
    {
        if (_connected)
            return;

        _pinned = GCHandle.Alloc(this);
        unsafe
        {
            ObsNative.signal_handler_connect(
                ObsNative.obs_output_get_signal_handler(_output.Pointer), "stop",
                &ObsOutput.OnStop, GCHandle.ToIntPtr(_pinned));
        }

        _connected = true;
    }

    // Removes the native callback and frees the pin. Only ever runs against a live output: the caller
    // disposes the output (which calls Disconnect) before libobs shuts down, so the signal handler
    // pointer is still valid here.
    internal void Disconnect()
    {
        if (!_connected)
            return;

        unsafe
        {
            ObsNative.signal_handler_disconnect(
                ObsNative.obs_output_get_signal_handler(_output.Pointer), "stop",
                &ObsOutput.OnStop, GCHandle.ToIntPtr(_pinned));
        }

        _pinned.Free();
        _connected = false;
    }

    internal void Add(EventHandler<ObsOutputStopEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers.Add(handler);
    }

    internal void Remove(EventHandler<ObsOutputStopEvent> handler) => _handlers.Remove(handler);

    // Called from the native stop signal, on a libobs thread. Reads code and last_error from the
    // calldata — the whole point, because obs_output_get_last_error may already be reset — then
    // raises the managed event on the thread that subscribed.
    internal unsafe void OnNativeStop(nint calldata)
    {
        // calldata ints are long long [calldata.h]; reading into a 32-bit int reads half of it.
        long code = 0;
        ObsNative.calldata_get_data(calldata, "code", &code, sizeof(long));

        string? lastError = null;
        nint errorPointer;
        if (ObsNative.calldata_get_string(calldata, "last_error", &errorPointer))
            lastError = Utf8Marshal.ReadBorrowed(errorPointer);

        var stop = new ObsOutputStopEvent((ObsOutputStopCode)code, lastError);
        if (_context is null)
        {
            foreach (var handler in _handlers.ToArray())
                handler(_output, stop);
        }
        else
        {
            foreach (var handler in _handlers.ToArray())
            {
                var captured = handler;
                _context.Post(_ => captured(_output, stop), null);
            }
        }
    }
}
