// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;
using Tript.Obs.Interop;

namespace Tript.Obs;

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

            // A Disconnect that raced an in-flight callback leaves the pin allocated until that
            // callback exits. Allocating a fresh one over it leaked the old handle permanently and
            // then freed the new one on the next exit. The pin is always for this object, so reuse it.
            if (!_pinned.IsAllocated)
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
