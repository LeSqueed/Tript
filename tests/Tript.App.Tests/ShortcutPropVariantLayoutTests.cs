// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace Tript.App.Tests;

public sealed class ShortcutPropVariantLayoutTests
{
    // The Start Menu shortcut's AppUserModelID goes through IPropertyStore.SetValue and is then
    // released with PropVariantClear, both of which touch a full native PROPVARIANT. A struct shorter
    // than that corrupts the stack of whoever called EnsureStartMenuShortcut.
    [Fact]
    public void ThePropVariantIsAsLargeAsTheNativeStructure()
    {
        var type = typeof(Tript.Shell.WindowsAppIdentity)
            .GetNestedType("PropVariant", BindingFlags.NonPublic);

        Assert.NotNull(type);

        var minimum = 8 + (2 * IntPtr.Size);
        Assert.True(Marshal.SizeOf(type!) >= minimum,
            $"PROPVARIANT must be at least {minimum} bytes, was {Marshal.SizeOf(type!)}");
    }
}
