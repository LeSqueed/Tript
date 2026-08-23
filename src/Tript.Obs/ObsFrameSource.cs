// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tript.Obs.Interop;

namespace Tript.Obs;

// The frame source behind the FrameSourceRegistry seam. It exists so a consumer can ask for raw
// composited frames in a format and size of its choosing and get either exactly that or a refused
// subscription — never a silent mismatch.
//
// The subscription uses the stable core raw-video callback. video_output_connect2 accepts the
// subscription on OBS 31.x but does not deliver frames there.
//
// The video_t handle comes from ObsRuntime.TryGetVideoHandle, which is gated on HasVideo because
// obs_get_video() itself segfaults when no video mix exists — measured on 32.2.1.
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
            // DEFAULT is a request to resolve, which is libobs's own semantic; handing a concrete
            // range here would bake in a decision that belongs to the compositor.
            Range = (int)ObsVideoRange.Default,
            Colorspace = (int)ObsColorSpace.Default
        };

        var subscription = new ObsFrameSubscription(_runtime, video, format, conversion, frameRateDivisor, callback);

        // The raw callback API has no success return. The target is registered first so a frame
        // delivered synchronously by libobs during registration can resolve the subscription.
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

        // The frame rate is a property of the video output, not of any subscription or frame:
        // struct video_data has no rate field at all, and video_output_get_info is where the
        // configured fraction actually lives. Null means no video pipeline, the only state in
        // which timing is absent.
        var info = (VideoOutputInfoNative*)ObsNative.video_output_get_info(video);
        if (info == null)
            return null;

        return new VideoTiming(info->FpsNumerator, info->FpsDenominator);
    }

    // The seam's format vocabulary is its own, not libobs's. FramePixelFormat.Bgra is member 0 of
    // its enum but VIDEO_FORMAT_BGRA is member 7 of enum video_format, so the value passes across
    // the P/Invoke boundary through an explicit map rather than a cast.
    private static int ToNativeFormat(FramePixelFormat format) => format switch
    {
        FramePixelFormat.Bgra => (int)ObsVideoFormat.Bgra,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format,
            $"The frame source does not know how to request {format}.")
    };
}

// One native subscription. libobs's identity for an input is the (callback, param) pair, so param
// has to be unique per subscription and has to survive being handed back to a callback that is
// already in flight when the subscription goes away.
//
// A GCHandle would be the obvious param and is the wrong one: OnFrame has to dereference param
// before it can check anything, and Dispose cannot free the handle without racing that dereference
// — video_output_disconnect returns without waiting for an in-flight callback. Freeing it in that
// window either resolves a recycled slot or throws across an UnmanagedCallersOnly frame, which
// terminates the process. So param is a never-reused id and the lookup is a dictionary: a callback
// that arrives after Dispose misses and returns, and the entry is a strong root while it is there.
internal sealed class ObsFrameSubscription : IFrameSubscription
{
    private static readonly ConcurrentDictionary<nint, ObsFrameSubscription> Live = new();
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

    // Registers the native callback. Called once, from Subscribe, before the subscription is ever
    // handed to a consumer.
    internal void Connect()
    {
        // Registered before the native call: libobs may deliver a frame from its own thread before
        // registration returns here.
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

        // The teardown race: obs_remove_raw_video_callback returns without waiting for an in-flight
        // callback, and a callback already inside OnFrame holds a strong reference to _target.
        // Swapping _target out first means that callback observes null and returns, while the
        // target object it captured stays alive until the callback itself is done.
        Volatile.Write(ref _target, null);

        if (_connected)
        {
            unsafe
            {
                ObsNative.obs_remove_raw_video_callback(&ObsFrameSubscription.OnFrame, _id);
            }
        }

        _connected = false;

        // Unregistering after the native removal, and the id is never reused, so a callback still in
        // flight resolves either this subscription (whose _target is already null) or nothing.
        Live.TryRemove(_id, out _);

        lock (_callbackGate)
        {
            var ownCallbacks = _callbackDepth.Value;
            while (_callbacksInFlight > ownCallbacks)
                Monitor.Wait(_callbackGate);
        }
    }

    // The native callback, invoked on the video output's own dedicated thread. The video_data it
    // receives is only valid for the duration of the call — unconverted frames point into the
    // output's 16-slot cache and converted frames rotate through three per-subscription buffers —
    // so the frame view handed to the consumer carries the same lifetime, enforced at the type
    // level by VideoFrame being a ref struct: it cannot be stored anywhere that outlives the call.
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
        catch
        {
            // Nothing may throw across the native frame; an exception escaping an
            // UnmanagedCallersOnly method terminates the process. A consumer whose callback throws
            // loses that frame and the connection survives.
        }
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

    // Captures the consumer's delegate and the subscription's format and dimensions so a
    // subscription can be disposed without racing its own callback: Dispose swaps the
    // subscription's reference to this object away, and a callback already in flight keeps the
    // strong reference it captured at entry for the rest of its run. The delegate it calls is the
    // consumer's, which must copy anything it needs out of the frame before returning — the plane
    // pointers are not valid afterwards.
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

            // Stack spans over the native video_data, so the view reads the pointers and strides
            // directly from the frame libobs handed us. The fixed-buffer long values are pointers
            // on every platform this binding targets; the span element type is nint, which is why
            // they are re-typed rather than passed through as long.
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
