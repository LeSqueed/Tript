// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;
using Tript.Obs.Interop;
using Tript.Core;

namespace Tript.Obs;

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

            // A Disconnect that timed out leaves the pin allocated for the callback still running.
            if (!_pinned.IsAllocated)
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

            // Unbounded, this self-deadlocked whenever Disconnect ran on a thread that was itself
            // inside a handler, or that pumped the SynchronizationContext a posted handler needed.
            // On timeout the pin stays allocated: freeing it under a live callback hands libobs a
            // dangling GCHandle.
            var deadline = Environment.TickCount64 + (long)DisconnectTimeout.TotalMilliseconds;
            while (_callbacksInFlight != 0)
            {
                var remaining = (int)Math.Max(0, deadline - Environment.TickCount64);
                if (remaining == 0 || !Monitor.Wait(_gate, remaining))
                {
                    Diagnostics.Report(DiagnosticLevel.Error,
                        $"OBS '{_signal}' handlers were still running after {DisconnectTimeout.TotalSeconds:0}s; "
                        + "leaving the callback pinned rather than freeing it under them");
                    return;
                }
            }

            if (_pinned.IsAllocated)
                _pinned.Free();
        }
    }

    private static readonly TimeSpan DisconnectTimeout = TimeSpan.FromSeconds(10);

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
