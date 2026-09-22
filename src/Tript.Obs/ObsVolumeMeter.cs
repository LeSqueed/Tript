// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tript.Obs.Interop;
using Tript.Core;

namespace Tript.Obs;

internal sealed class ObsVolumeMeter : IDisposable
{
    private const int IecFader = 1;
    private const int MaxAudioChannels = 8;
    private const float MeterDbFloor = -60f;
    private const float MeterDbCeiling = 0f;
    private static readonly ConcurrentDictionary<nint, ObsVolumeMeter> Live = new();

    // ObsRuntime.Dispose calls this before obs_shutdown. An instance left registered past shutdown
    // would, on its owner's later Dispose, call back into a libobs that no longer exists.
    internal static void DisposeAllLive()
    {
        foreach (var live in Live.Values.ToArray())
            live.Dispose();
    }
    private static long _nextId;

    private readonly ObsSource _source;
    private readonly ObsVolumeMeterHandle _handle;
    private readonly nint _id;
    private readonly object _callbackGate = new();
    private readonly ThreadLocal<int> _callbackDepth = new();
    private int _callbacksInFlight;
    private int _disposed;
    private float _peak;
    private long _lastUpdate;

    internal ObsVolumeMeter(ObsSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        var pointer = ObsNative.obs_volmeter_create(IecFader);
        if (pointer == nint.Zero)
            throw new ObsException("obs_volmeter_create returned null.");

        _handle = new ObsVolumeMeterHandle(pointer);
        _id = (nint)Interlocked.Increment(ref _nextId);
        try
        {
            Live[_id] = this;
            unsafe
            {
                ObsNative.obs_volmeter_add_callback(pointer, &OnUpdated, _id);
            }

            if (!ObsNative.obs_volmeter_attach_source(pointer, source.Pointer))
                throw new ObsException("obs_volmeter_attach_source failed.");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal float Peak => Environment.TickCount64 - Volatile.Read(ref _lastUpdate) <= 750
        ? Volatile.Read(ref _peak)
        : 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var pointer = _handle.DangerousGetHandle();
        unsafe
        {
            ObsNative.obs_volmeter_remove_callback(pointer, &OnUpdated, _id);
        }
        ObsNative.obs_volmeter_detach_source(pointer);
        Live.TryRemove(_id, out _);

        lock (_callbackGate)
        {
            var ownCallbacks = _callbackDepth.Value;
            while (_callbacksInFlight > ownCallbacks)
                Monitor.Wait(_callbackGate);
        }

        _handle.Dispose();
        GC.KeepAlive(_source);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnUpdated(nint parameter, float* magnitude, float* peak, float* inputPeak)
    {
        try
        {
            if (!Live.TryGetValue(parameter, out var meter) || !meter.TryEnterCallback())
                return;

            try
            {
                meter._callbackDepth.Value++;
                var value = float.NegativeInfinity;
                for (var channel = 0; channel < MaxAudioChannels; channel++)
                {
                    var sample = inputPeak[channel];
                    if (sample > value)
                        value = sample;
                }
                Volatile.Write(ref meter._peak, ToMeterPosition(value));
                Volatile.Write(ref meter._lastUpdate, Environment.TickCount64);
            }
            finally
            {
                meter._callbackDepth.Value--;
                meter.ExitCallback();
            }
        }
        catch (Exception exception)
        {
            Diagnostics.ReportFirst(ref _meterCallbackFailed, DiagnosticLevel.Warning,
                "libobs volume meter callback failed; audio level meters are not updating", exception);
        }
    }

    private static int _meterCallbackFailed;

    internal static float ToMeterPosition(float decibels)
    {
        if (!float.IsFinite(decibels))
            return 0;
        var ratio = (decibels - MeterDbFloor) / (MeterDbCeiling - MeterDbFloor);
        return Math.Clamp(ratio, 0, 1);
    }

    private bool TryEnterCallback()
    {
        lock (_callbackGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
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
            if (_callbacksInFlight == 0)
                Monitor.PulseAll(_callbackGate);
        }
    }
}
