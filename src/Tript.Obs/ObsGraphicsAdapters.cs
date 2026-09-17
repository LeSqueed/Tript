// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tript.Obs.Interop;

namespace Tript.Obs;

public static class ObsGraphicsAdapters
{
    public static unsafe string? CurrentAdapterName(ObsRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        if (!runtime.TryGetVideoInfo(out var video) || video is null)
            return null;

        var found = new AdapterLookup(video.Adapter);
        var handle = GCHandle.Alloc(found);
        ObsNative.obs_enter_graphics();
        try
        {
            ObsNative.gs_enum_adapters(&OnAdapter, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            ObsNative.obs_leave_graphics();
            handle.Free();
        }

        return found.Name;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte OnAdapter(nint parameter, nint name, uint index)
    {
        if (GCHandle.FromIntPtr(parameter).Target is not AdapterLookup lookup || index != lookup.Index)
            return 1;

        lookup.Name = Marshal.PtrToStringUTF8(name);
        return 0;
    }

    private sealed class AdapterLookup(uint index)
    {
        internal uint Index { get; } = index;

        internal string? Name { get; set; }
    }
}
