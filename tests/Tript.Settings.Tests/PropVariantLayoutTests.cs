// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace Tript.Settings.Tests;

public class PropVariantLayoutTests
{
    // IPropertyStore.GetValue writes, and PropVariantClear zeroes, a full native PROPVARIANT. If the
    // managed struct is smaller than that, both scribble over the caller's stack. The enumerator runs
    // on a 5-second timer, so a short struct is a slow, silent corruption rather than a clean crash.
    [Fact]
    public void ThePropVariantIsAsLargeAsTheNativeStructure()
    {
        var type = typeof(WasapiDeviceEnumerator)
            .GetNestedType("PropVariant", BindingFlags.NonPublic);

        Assert.NotNull(type);

        var expected = 8 + (2 * IntPtr.Size);
        Assert.Equal(expected, Marshal.SizeOf(type!));
    }
}
