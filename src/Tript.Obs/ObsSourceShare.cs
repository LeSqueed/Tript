// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tript.Obs.Interop;

namespace Tript.Obs;

public readonly record struct SharedFrameSize(uint Width, uint Height);

internal sealed unsafe class ObsSourceShare : IDisposable
{
    private const float OrthoDepth = 100f;

    private static readonly ConcurrentDictionary<nint, ObsSourceShare> Live = new();
    private static long _nextId;

    private readonly ObsSource _source;
    private readonly ISharedTextureSink _sink;
    private readonly nint _id;
    private readonly object _callbackGate = new();
    private readonly float* _clearColour;
    private int _callbacksInFlight;
    private int _disposed;
    private bool _connected;

    private nint _texrender;
    private nint _texture;
    private uint _width;
    private uint _height;
    private bool _published;
    private bool _holdsFrame;
    private int _textureRefused;
    private long _refusedSize;
    private long _frameSize;

    private ObsSourceShare(ObsSource source, ISharedTextureSink sink)
    {
        _source = source.AddReference();
        _sink = sink;
        _id = (nint)Interlocked.Increment(ref _nextId);
        _clearColour = (float*)NativeMemory.AlignedAlloc(8 * sizeof(float), 16);
        new Span<float>(_clearColour, 8).Clear();
        _clearColour[3] = 1f;
    }

    internal event Action? FrameSizeChanged;

    internal SharedFrameSize? FrameSize
    {
        get
        {
            var packed = Volatile.Read(ref _frameSize);
            return packed == 0 ? null : new SharedFrameSize((uint)(packed >> 32), (uint)packed);
        }
    }

    internal bool TextureRefused => Volatile.Read(ref _textureRefused) != 0;

    internal static ObsSourceShare Start(ObsSource source, ISharedTextureSink sink)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);

        var share = new ObsSourceShare(source, sink);
        Live[share._id] = share;
        ObsNative.obs_add_main_rendered_callback(&OnRendered, share._id);
        share._connected = true;
        return share;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_connected)
            ObsNative.obs_remove_main_rendered_callback(&OnRendered, _id);
        _connected = false;
        Live.TryRemove(_id, out _);

        lock (_callbackGate)
        {
            while (_callbacksInFlight > 0)
                Monitor.Wait(_callbackGate);
        }

        ObsNative.obs_enter_graphics();
        try
        {
            DestroyTexture();
            if (_texrender != nint.Zero)
                ObsNative.gs_texrender_destroy(_texrender);
            _texrender = nint.Zero;
        }
        finally
        {
            ObsNative.obs_leave_graphics();
        }

        _sink.Withdraw();
        NativeMemory.AlignedFree(_clearColour);
        _source.Dispose();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRendered(nint parameter)
    {
        try
        {
            if (!Live.TryGetValue(parameter, out var share) || !share.TryEnterCallback())
                return;

            try
            {
                share.Render();
            }
            finally
            {
                share.ExitCallback();
            }
        }
        catch
        {
        }
    }

    private void Render()
    {
        var source = _source.Pointer;
        var width = ObsNative.obs_source_get_width(source);
        var height = ObsNative.obs_source_get_height(source);

        if (width == 0 || height == 0)
        {
            if (_holdsFrame)
                BlankSharedFrame();
            SetFrameSize(0, 0);
            return;
        }

        if (!EnsureTargets(width, height))
            return;

        ObsNative.gs_texrender_reset(_texrender);
        if (!ObsNative.gs_texrender_begin_with_color_space(_texrender, width, height, (int)ObsSourceColorSpace.Srgb))
            return;

        try
        {
            ObsNative.gs_clear(ObsNative.GsClearColor, OpaqueBlack, 0f, 0);
            ObsNative.gs_ortho(0f, width, 0f, height, -OrthoDepth, OrthoDepth);
            ObsNative.gs_blend_state_push();
            ObsNative.gs_enable_blending(true);
            ObsNative.gs_enable_color(true, true, true, false);
            try
            {
                ObsNative.obs_source_video_render(source);
            }
            finally
            {
                ObsNative.gs_enable_color(true, true, true, true);
                ObsNative.gs_blend_state_pop();
            }
        }
        finally
        {
            ObsNative.gs_texrender_end(_texrender);
        }

        CopyToShared();

        if (!_published)
        {
            _sink.Publish(ObsNative.gs_texture_get_shared_handle(_texture), width, height);
            _published = true;
        }

        _holdsFrame = true;
        SetFrameSize(width, height);
    }

    private bool EnsureTargets(uint width, uint height)
    {
        if (_texrender == nint.Zero)
            _texrender = ObsNative.gs_texrender_create(ObsNative.GsColorFormatBgra, ObsNative.GsZStencilNone);

        if (_texture != nint.Zero && _width == width && _height == height)
            return _texrender != nint.Zero;

        var requested = ((long)width << 32) | height;
        if (_refusedSize == requested)
            return false;

        DestroyTexture();

        _texture = ObsNative.gs_texture_create(width, height, ObsNative.GsColorFormatBgra, 1, nint.Zero,
            ObsNative.GsTextureRenderTarget | ObsNative.GsTextureShared);
        var handle = _texture == nint.Zero ? 0 : ObsNative.gs_texture_get_shared_handle(_texture);
        if (_texrender == nint.Zero || handle is 0 or ObsNative.GsInvalidHandle)
        {
            _refusedSize = requested;
            if (Interlocked.Exchange(ref _textureRefused, 1) == 0)
                FrameSizeChanged?.Invoke();

            DestroyTexture();
            return false;
        }

        _width = width;
        _height = height;
        _published = false;
        return true;
    }

    private void CopyToShared()
    {
        var locked = _sink.TryBeginFrame();
        if (!locked && _published)
            return;

        try
        {
            ObsNative.gs_copy_texture(_texture, ObsNative.gs_texrender_get_texture(_texrender));
            ObsNative.gs_flush();
        }
        finally
        {
            if (locked)
                _sink.EndFrame();
        }
    }

    private void BlankSharedFrame()
    {
        _holdsFrame = false;
        if (_texture == nint.Zero)
            return;

        ObsNative.gs_texrender_reset(_texrender);
        if (!ObsNative.gs_texrender_begin_with_color_space(_texrender, _width, _height, (int)ObsSourceColorSpace.Srgb))
            return;

        try
        {
            ObsNative.gs_clear(ObsNative.GsClearColor, Transparent, 0f, 0);
        }
        finally
        {
            ObsNative.gs_texrender_end(_texrender);
        }

        CopyToShared();
    }

    private float* OpaqueBlack => _clearColour;

    private float* Transparent => _clearColour + 4;

    private void DestroyTexture()
    {
        if (_texture != nint.Zero)
            ObsNative.gs_texture_destroy(_texture);
        _texture = nint.Zero;
        _width = 0;
        _height = 0;
    }

    private void SetFrameSize(uint width, uint height)
    {
        var packed = width == 0 || height == 0 ? 0 : ((long)width << 32) | height;
        if (Interlocked.Exchange(ref _frameSize, packed) != packed)
            FrameSizeChanged?.Invoke();
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
