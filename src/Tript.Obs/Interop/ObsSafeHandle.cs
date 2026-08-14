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
//   * Owned by the caller, outliving the context — bmem allocations. BMemHandle.
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
