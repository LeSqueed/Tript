// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;

namespace Tript.Obs.Interop;

internal abstract class ObsSafeHandle : SafeHandle
{
    protected ObsSafeHandle(nint handle, bool ownsHandle) : base(nint.Zero, ownsHandle) => SetHandle(handle);

    public override bool IsInvalid => handle == nint.Zero;
}

internal abstract class ObsContextHandle : ObsSafeHandle
{
    private readonly long _generation;

    protected ObsContextHandle(nint handle, bool ownsHandle) : base(handle, ownsHandle) =>
        _generation = ObsRuntime.Generation;

    protected abstract void Release(nint handle);

    protected sealed override bool ReleaseHandle()
    {
        if (!ObsRuntime.TryEnterRelease(_generation))
            return true;

        try
        {
            Release(handle);
        }
        finally
        {
            ObsRuntime.ExitRelease();
        }

        return true;
    }
}

internal sealed class BMemHandle : ObsSafeHandle
{
    internal BMemHandle(nint handle) : base(handle, ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle()
    {
        ObsNative.bfree(handle);
        return true;
    }
}

internal sealed class ObsSettingsHandle : ObsSafeHandle
{
    internal ObsSettingsHandle(nint handle) : base(handle, ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle()
    {
        ObsNative.obs_data_release(handle);
        return true;
    }
}

internal sealed class ObsSourceHandle : ObsContextHandle
{
    internal ObsSourceHandle(nint handle) : base(handle, ownsHandle: true)
    {
    }

    protected override void Release(nint handle) => ObsNative.obs_source_release(handle);
}

internal sealed class ObsVolumeMeterHandle : ObsSafeHandle
{
    internal ObsVolumeMeterHandle(nint handle) : base(handle, ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle()
    {
        ObsNative.obs_volmeter_destroy(handle);
        return true;
    }
}

internal sealed class ObsSceneHandle : ObsContextHandle
{
    private readonly bool _registeredWithCore;

    internal ObsSceneHandle(nint handle, bool registeredWithCore) : base(handle, ownsHandle: true) =>
        _registeredWithCore = registeredWithCore;

    protected override void Release(nint handle)
    {
        if (_registeredWithCore)
            ObsNative.obs_source_remove(ObsNative.obs_scene_get_source(handle));

        ObsNative.obs_scene_release(handle);
    }
}

internal sealed class ObsSceneItemHandle : ObsContextHandle
{
    internal ObsSceneItemHandle(nint handle) : base(handle, ownsHandle: true)
    {
    }

    protected override void Release(nint handle) => ObsNative.obs_sceneitem_release(handle);
}

internal sealed class ObsEncoderHandle : ObsContextHandle
{
    internal ObsEncoderHandle(nint handle) : base(handle, ownsHandle: true)
    {
    }

    protected override void Release(nint handle) => ObsNative.obs_encoder_release(handle);
}

internal sealed class ObsOutputHandle : ObsContextHandle
{
    internal ObsOutputHandle(nint handle) : base(handle, ownsHandle: true)
    {
    }

    protected override void Release(nint handle) => ObsNative.obs_output_release(handle);
}

internal sealed class ObsWeakSourceHandle : ObsSafeHandle
{
    internal ObsWeakSourceHandle(nint handle) : base(handle, ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle()
    {
        ObsNative.obs_weak_source_release(handle);
        return true;
    }
}

internal sealed class ObsSettingsArrayHandle : ObsSafeHandle
{
    internal ObsSettingsArrayHandle(nint handle) : base(handle, ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle()
    {
        ObsNative.obs_data_array_release(handle);
        return true;
    }
}
