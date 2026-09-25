// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Shell.Linux;

internal static class X11KeyGrabs
{
    internal const uint ShiftMask = 1 << 0;
    internal const uint LockMask = 1 << 1;
    internal const uint ControlMask = 1 << 2;
    internal const uint Mod1Mask = 1 << 3;
    internal const uint Mod2Mask = 1 << 4;
    internal const uint Mod4Mask = 1 << 6;

    private const uint SignificantMask = ShiftMask | ControlMask | Mod1Mask | Mod4Mask;

    internal static readonly uint[] IgnoredModifierVariants = [0, LockMask, Mod2Mask, LockMask | Mod2Mask];

    internal static uint FromBindingModifiers(uint modifiers)
    {
        var mask = 0u;
        if ((modifiers & HotkeyBindingFormat.ModShift) != 0)
            mask |= ShiftMask;
        if ((modifiers & HotkeyBindingFormat.ModControl) != 0)
            mask |= ControlMask;
        if ((modifiers & HotkeyBindingFormat.ModAlt) != 0)
            mask |= Mod1Mask;
        if ((modifiers & HotkeyBindingFormat.ModWin) != 0)
            mask |= Mod4Mask;
        return mask;
    }

    internal static IEnumerable<uint> GrabMasks(uint mask) =>
        IgnoredModifierVariants.Select(variant => mask | variant);

    internal static uint Significant(uint state) => state & SignificantMask;
}

internal sealed class X11RepeatFilter
{
    private readonly HashSet<uint> _held = [];

    internal bool Pressed(uint keycode) => _held.Add(keycode);

    internal void Released(uint keycode) => _held.Remove(keycode);

    internal void Clear() => _held.Clear();
}
