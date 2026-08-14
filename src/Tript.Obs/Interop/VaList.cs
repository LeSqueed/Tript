// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;

namespace Tript.Obs.Interop;

// The System V AMD64 va_list: __va_list_tag, of which va_list is an array of one. Because it is an
// array type it decays to a pointer when passed as an argument, so a callback receives the address
// of this struct — which is why the parameter can be declared as a pointer even though the type is
// not one. Windows x64 has no equivalent: there va_list really is a char* walking the stack.
//
// The distinction only becomes visible when the list is copied. Passing the incoming pointer
// straight back to a printf leaves the caller's list consumed, and reading arguments twice from a
// consumed list yields whatever was next on the stack.
[StructLayout(LayoutKind.Sequential)]
internal struct VaListSystemV
{
    public uint GpOffset;
    public uint FpOffset;
    public nint OverflowArgArea;
    public nint RegSaveArea;
}

internal static unsafe class VaListFormatter
{
    // Applies a C format string to a va_list and returns the result. Formatting goes through
    // libobs's own dstr_vprintf: it is exported on both platforms, and it avoids naming libc, whose
    // shared object is libc.so.6 on glibc and something else everywhere else.
    internal static string Format(nint format, nint arguments)
    {
        if (format == nint.Zero)
            return string.Empty;

        // A caller with no variadic arguments is entitled to pass none. The format string is then
        // literal text, and running it through printf would misread any percent it contains.
        if (arguments == nint.Zero)
            return Utf8Marshal.ReadBorrowed(format) ?? string.Empty;

        DStrNative destination = default;

        if (OperatingSystem.IsWindows())
        {
            ObsNative.dstr_vprintf(&destination, format, arguments);
        }
        else
        {
            // va_copy, by hand. The System V ABI defines it as a copy of the four fields — the save
            // areas are shared and only read — so a struct copy is the whole of it. Doing it means
            // the handler never consumes the list it was given, which keeps chaining to a previous
            // log handler possible and keeps a second read of the same list correct.
            var copy = *(VaListSystemV*)arguments;
            ObsNative.dstr_vprintf(&destination, format, (nint)(&copy));
        }

        try
        {
            return Utf8Marshal.ReadBorrowed(destination.Array) ?? string.Empty;
        }
        finally
        {
            // dstr's buffer is a bmem allocation and dstr_free is a static inline, so the free is
            // ours to make.
            ObsNative.bfree(destination.Array);
        }
    }
}
