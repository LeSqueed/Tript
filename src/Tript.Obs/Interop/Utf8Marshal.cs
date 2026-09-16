// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;

namespace Tript.Obs.Interop;

internal static class Utf8Marshal
{
    // libobs keeps ownership and may invalidate the pointer on the next call, so copy.
    internal static string? ReadBorrowed(nint pointer) =>
        pointer == nint.Zero ? null : Marshal.PtrToStringUTF8(pointer);

    // bmem is not the CRT heap: free through libobs.
    internal static string? ReadOwned(nint pointer)
    {
        if (pointer == nint.Zero)
            return null;

        using var owned = new BMemHandle(pointer);
        return Marshal.PtrToStringUTF8(pointer);
    }

    // Caller frees: libobs only reads these.
    internal static nint Allocate(string value) => Marshal.StringToCoTaskMemUTF8(value);

    internal static void Free(nint pointer) => Marshal.FreeCoTaskMem(pointer);
}
