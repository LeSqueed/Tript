// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;

namespace Tript.Obs.Interop;

// libobs speaks UTF-8 in both directions and never UTF-16. The two directions have different
// ownership rules, so they get different helpers rather than one that guesses.
internal static class Utf8Marshal
{
    // For a pointer libobs still owns — obs_get_locale, obs_get_version_string, the id strings from
    // the type enumerators. Copying is the point: the pointer may be invalidated by the next call.
    internal static string? ReadBorrowed(nint pointer) =>
        pointer == nint.Zero ? null : Marshal.PtrToStringUTF8(pointer);

    // For a pointer libobs handed over — obs_find_data_file and friends, each of which says "use
    // bfree after use". bmem is not necessarily the process CRT heap, so the free has to go back
    // through libobs.
    internal static string? ReadOwned(nint pointer)
    {
        if (pointer == nint.Zero)
            return null;

        using var owned = new BMemHandle(pointer);
        return Marshal.PtrToStringUTF8(pointer);
    }

    // Only for strings that outlive a single call frame or sit inside a struct libobs reads — the
    // graphics module name in obs_video_info is the one case here. Plain arguments are
    // marshalled by the generated stub instead. libobs only ever reads these, so allocating them on
    // the managed side is safe as long as the caller frees them.
    internal static nint Allocate(string value) => Marshal.StringToCoTaskMemUTF8(value);

    internal static void Free(nint pointer) => Marshal.FreeCoTaskMem(pointer);
}
