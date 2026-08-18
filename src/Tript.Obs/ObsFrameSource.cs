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
// The subscription is the media-io pair on the main video mix's video_t, never the core
// obs_add_raw_video_callback2:
//
//   * video_output_connect2 returns the boolean the core function discards, and that boolean is
//     the only signal for two of the three refusal modes — an unsatisfiable conversion (the scaler
//     could not be created) and a duplicate (callback, param) pair. It also rejects a zero
//     frame_rate_divisor.
//   * video_output_disconnect returns void, and that loses nothing: its bool-reporting sibling,
//     video_output_disconnect2, exists only from 31.1.2, and its boolean reports whether the input
//     was found — not whether the callback is quiescent, which is the thing that actually matters
//     at teardown. (See ObsFrameSubscription.Dispose for how quiescence is actually bought.)
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

        // video_output_connect2 is the one place the seam can be refused. Throwing here rather
        // than handing out a subscription that silently delivers nothing is the whole reason the
        // media-io pair is bound at all.
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
    // handed to a consumer — so a refused connection is observable at Subscribe and never as a
    // dead subscription.
    internal void Connect()
    {
        // Registered before the connect, not after: libobs may deliver a frame from its own thread
        // before video_output_connect2 has returned here.
        Live[_id] = this;
        unsafe
        {
            if (!ObsNative.video_output_connect2(_video, in _conversion, _frameRateDivisor,
                    &ObsFrameSubscription.OnFrame, _id))
            {
                throw new ObsException(
                    "video_output_connect2 refused the subscription: the conversion cannot be " +
                    "satisfied, the (callback, param) pair is already connected, or the frame-rate " +
                    "divisor is zero.");
            }
        }

        _connected = true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // The teardown race: video_output_disconnect returns without waiting for an in-flight
        // callback, and a callback already inside OnFrame holds a strong reference to _target.
        // Swapping _target out first means that callback observes null and returns, while the
        // target object it captured stays alive until the callback itself is done.
        Volatile.Write(ref _target, null);

        // A subscription whose video mix was torn down by obs_reset_video must not disconnect
        // from the freed handle. The check is cheap (one obs_get_video_info probe) and safe
        // because Dispose runs on the control plane, which holds libobs's global lock for the
        // duration of a reset — no reset can land between the probe and the disconnect.
        if (_connected && _runtime.IsCurrentVideoHandle(_video))
        {
            unsafe
            {
                ObsNative.video_output_disconnect(_video, &ObsFrameSubscription.OnFrame, _id);
            }
        }

        _connected = false;

        // Unregistering after the disconnect, and the id is never reused, so a callback still in
        // flight resolves either this subscription (whose _target is already null) or nothing.
        Live.TryRemove(_id, out _);
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

            var target = subscription._target;
            if (target is null)
                return;

            target.Deliver((VideoDataNative*)framePointer);
        }
        catch
        {
            // Nothing may throw across the native frame; an exception escaping an
            // UnmanagedCallersOnly method terminates the process. A consumer whose callback throws
            // loses that frame and the connection survives.
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
