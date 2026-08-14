// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;

namespace Tript.Obs.Interop;

// Nothing libobs returns is garbage-collected, so this file decides once how ownership is expressed.
// Sources, outputs and encoders will each need dozens of these, and they inherit whatever is decided
// here.
//
// Three kinds of pointer come back from libobs, and the binding keeps them visibly distinct:
//
//   * Borrowed for the duration of a call — id strings, names. Read immediately into managed
//     memory; never stored, never wrapped.
//   * Owned by the OBS context — sources, outputs, encoders. ObsContextHandle.
//   * Owned by the caller, outliving the context — bmem allocations, and the refcounted settings
//     objects. BMemHandle, ObsSettingsHandle, ObsSettingsArrayHandle.
//
// SafeHandle rather than a raw pointer plus try/finally because the failure it prevents is
// invisible: a leaked libobs object keeps a device, a thread or a file open, and nothing reports
// it. The finalizer is the backstop for the paths that forget.
internal abstract class ObsSafeHandle : SafeHandle
{
    protected ObsSafeHandle(nint handle, bool ownsHandle) : base(nint.Zero, ownsHandle) => SetHandle(handle);

    public override bool IsInvalid => handle == nint.Zero;
}

// For objects the OBS context owns. obs_shutdown destroys all of them at once, so a release after
// shutdown is a use-after-free — and because finalizers run whenever the GC decides, that ordering
// is the ordinary case rather than a rare race. The generation stamp is what makes it safe:
// a handle created before a shutdown declines to release afterwards, because there is nothing left
// to release.
internal abstract class ObsContextHandle : ObsSafeHandle
{
    private readonly long _generation;

    protected ObsContextHandle(nint handle, bool ownsHandle) : base(handle, ownsHandle) =>
        _generation = ObsRuntime.Generation;

    // True once the context that created this handle has been shut down. Callers that would
    // otherwise pass a stale pointer back into libobs should check it.
    internal bool IsStale => ObsRuntime.Generation != _generation;

    protected abstract void Release(nint handle);

    protected sealed override bool ReleaseHandle()
    {
        if (IsStale)
            return true;

        Release(handle);
        return true;
    }
}

// A bmem allocation the caller must free. Deliberately not an ObsContextHandle: bmem outlives
// obs_shutdown, so freeing one after the context is gone is correct rather than a fault, and
// skipping it would leak.
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

// A refcounted settings object. Deliberately **not** an ObsContextHandle, which is the assumption
// the shape of obs_data invites and which measurement refutes: on 32.2.1 obs_data_create succeeds
// before obs_startup, an object created inside a context still reads its values after
// obs_shutdown, and obs_data_release afterwards neither crashes nor leaks. obs_data lives on bmem
// and the OBS core holds no registry of these objects, so its lifetime is the caller's alone.
//
// The consequence for the generation stamp is the one that matters: an ObsContextHandle would
// *decline* to release a settings object that outlived a shutdown, which here would be a genuine
// leak rather than the use-after-free it prevents for sources and encoders.
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

// A source, a scene or a scene item. All three are ObsContextHandles, and unlike obs_data that was
// measured rather than assumed: obs_shutdown frees every one of these whether or not the caller
// still holds a reference — a source deliberately leaked across a shutdown left the allocation count
// back at zero — so a release afterwards is a use-after-free, which is what the generation stamp
// declines to perform.
internal sealed class ObsSourceHandle : ObsContextHandle
{
    internal ObsSourceHandle(nint handle) : base(handle, ownsHandle: true)
    {
    }

    protected override void Release(nint handle) => ObsNative.obs_source_release(handle);
}

// A scene, which is a source with one measured difference: obs_scene_create registers the scene with
// the OBS core, and that registration is a reference of its own. Releasing the caller's reference
// leaves the scene alive and still findable by name, so a handle for such a scene marks the source
// removed first — the call that makes the core let go. A private scene has no such registration and
// is destroyed by the release alone.
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

// A scene item. obs_scene_add returns a *borrowed* pointer — the single reference it creates belongs
// to the scene — so every handle takes its own reference first and this release balances that one,
// never the scene's. Releasing a borrowed item instead frees it while the scene still lists it,
// which measurement shows leaves the scene holding a dangling pointer rather than failing.
internal sealed class ObsSceneItemHandle : ObsContextHandle
{
    internal ObsSceneItemHandle(nint handle) : base(handle, ownsHandle: true)
    {
    }

    protected override void Release(nint handle) => ObsNative.obs_sceneitem_release(handle);
}

// A weak source reference. Deliberately not an ObsContextHandle, for the same reason as obs_data and
// on the same evidence: the control block is a bmem allocation that survives obs_shutdown, and the
// count only returns to zero once it is released. Declining to release it after a shutdown would
// leak it.
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

// Same ownership rules as ObsSettingsHandle; a separate type because obs_data_array_t has its own
// release and passing one where the other is expected would corrupt a refcount silently.
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
