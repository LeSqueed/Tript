// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;

namespace Tript.Obs.Interop;

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
    internal static string Format(nint format, nint arguments)
    {
        if (format == nint.Zero)
            return string.Empty;

        if (arguments == nint.Zero)
            return Utf8Marshal.ReadBorrowed(format) ?? string.Empty;

        DStrNative destination = default;

        if (OperatingSystem.IsWindows())
        {
            ObsNative.dstr_vprintf(&destination, format, arguments);
        }
        else
        {
            var copy = *(VaListSystemV*)arguments;
            ObsNative.dstr_vprintf(&destination, format, (nint)(&copy));
        }

        try
        {
            return Utf8Marshal.ReadBorrowed(destination.Array) ?? string.Empty;
        }
        finally
        {
            ObsNative.bfree(destination.Array);
        }
    }
}
