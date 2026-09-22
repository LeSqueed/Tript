// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tript.Obs.Interop;
using Tript.Core;

namespace Tript.Obs;

internal sealed class ObsFrameSource : IFrameSource
{
    private readonly ObsRuntime _runtime;

    internal ObsFrameSource(ObsRuntime runtime) => _runtime = runtime;

    public IFrameSubscription Subscribe(FramePixelFormat format, int width, int height,
        FrameCallback callback, uint frameRateDivisor)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        if (!_runtime.TryGetVideoHandle(out var video))
            throw new ObsException(
                "No video pipeline is running, so no frame subscription can be created. " +
                "The video mix exists only after a successful obs_reset_video.");

        var conversion = new VideoScaleInfoNative
        {
            Format = ToNativeFormat(format),
            Width = (uint)width,
            Height = (uint)height,

            Range = (int)ObsVideoRange.Default,
            Colorspace = (int)ObsColorSpace.Default
        };

        var subscription = new ObsFrameSubscription(_runtime, video, format, conversion, frameRateDivisor, callback);

        try
        {
            subscription.Connect();
        }
        catch
        {
            subscription.Dispose();
            throw;
        }

        return subscription;
    }

    public unsafe VideoTiming? GetVideoTiming()
    {
        if (!_runtime.TryGetVideoHandle(out var video))
            return null;

        var info = (VideoOutputInfoNative*)ObsNative.video_output_get_info(video);
        if (info == null)
            return null;

        return new VideoTiming(info->FpsNumerator, info->FpsDenominator, info->Width, info->Height);
    }

    private static int ToNativeFormat(FramePixelFormat format) => format switch
    {
        FramePixelFormat.Bgra => (int)ObsVideoFormat.Bgra,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format,
            $"The frame source does not know how to request {format}.")
    };
}

internal sealed class ObsFrameSubscription : IFrameSubscription
{
    private static readonly ConcurrentDictionary<nint, ObsFrameSubscription> Live = new();

    // ObsRuntime.Dispose calls this before obs_shutdown. An instance left registered past shutdown
    // would, on its owner's later Dispose, call back into a libobs that no longer exists.
    internal static void DisposeAllLive()
    {
        foreach (var live in Live.Values.ToArray())
            live.Dispose();
    }
    private static long _nextId;

    private readonly ObsRuntime _runtime;
    private readonly nint _video;
    private readonly FramePixelFormat _format;
    private readonly VideoScaleInfoNative _conversion;
    private readonly uint _frameRateDivisor;
    private readonly nint _id;
    private CallbackTarget? _target;
    private bool _connected;
    private int _disposed;
    private readonly object _callbackGate = new();
    private int _callbacksInFlight;
    private readonly ThreadLocal<int> _callbackDepth = new();

    internal ObsFrameSubscription(ObsRuntime runtime, nint video, FramePixelFormat format,
        VideoScaleInfoNative conversion, uint frameRateDivisor, FrameCallback callback)
    {
        _runtime = runtime;
        _video = video;
        _format = format;
        _conversion = conversion;
        _frameRateDivisor = frameRateDivisor;
        _id = (nint)Interlocked.Increment(ref _nextId);
        _target = new CallbackTarget(format, conversion.Width, conversion.Height, callback);
    }

    internal void Connect()
    {
        Live[_id] = this;
        unsafe
        {
            var conversion = _conversion;
            var conversionPointer = (nint)Unsafe.AsPointer(ref conversion);
            ObsNative.obs_add_raw_video_callback2(conversionPointer, _frameRateDivisor,
                &ObsFrameSubscription.OnFrame, _id);
        }

        _connected = true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Volatile.Write(ref _target, null);

        if (_connected)
        {
            unsafe
            {
                ObsNative.obs_remove_raw_video_callback(&ObsFrameSubscription.OnFrame, _id);
            }
        }

        _connected = false;

        Live.TryRemove(_id, out _);

        lock (_callbackGate)
        {
            var ownCallbacks = _callbackDepth.Value;
            while (_callbacksInFlight > ownCallbacks)
                Monitor.Wait(_callbackGate);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe void OnFrame(nint parameter, nint framePointer)
    {
        try
        {
            if (!Live.TryGetValue(parameter, out var subscription))
                return;

            if (!subscription.TryEnterCallback())
                return;

            try
            {
                subscription._callbackDepth.Value++;
                var target = Volatile.Read(ref subscription._target);
                if (target is not null)
                    target.Deliver((VideoDataNative*)framePointer);
            }
            finally
            {
                subscription._callbackDepth.Value--;
                subscription.ExitCallback();
            }
        }
        catch (Exception exception)
        {
            Diagnostics.ReportFirst(ref _frameCallbackFailed, DiagnosticLevel.Error,
                "libobs raw video callback failed; detection frames are not being delivered", exception);
        }
    }

    private static int _frameCallbackFailed;

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

    internal sealed class CallbackTarget
    {
        private readonly FramePixelFormat _format;
        private readonly uint _width;
        private readonly uint _height;
        private readonly FrameCallback _callback;

        internal CallbackTarget(FramePixelFormat format, uint width, uint height, FrameCallback callback)
        {
            _format = format;
            _width = width;
            _height = height;
            _callback = callback;
        }

        internal unsafe void Deliver(VideoDataNative* frame)
        {
            var planeCount = 0;
            for (var i = 0; i < 8; i++)
            {
                if (frame->Data[i] != 0)
                    planeCount = i + 1;
            }

            var view = new VideoFrame(
                _format,
                _width,
                _height,
                new ReadOnlySpan<nint>((nint*)frame->Data, planeCount),
                new ReadOnlySpan<uint>(frame->Linesize, planeCount));
            _callback(in view);
        }
    }
}
