// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;

namespace Tript.Shell.Linux;

internal readonly record struct KeySymbol(uint Value, string Name);

internal readonly record struct LinuxHotkey(HotkeyAction Action, uint Modifiers, KeySymbol Key);

internal static class LinuxKeySymbols
{
    private static readonly Dictionary<uint, KeySymbol> Named = new()
    {
        [0x20] = new(0x0020, "space"),
        [0x09] = new(0xFF09, "Tab"),
        [0xC0] = new(0x0060, "grave"),
        [0xBD] = new(0x002D, "minus"),
        [0xBB] = new(0x003D, "equal"),
        [0xDB] = new(0x005B, "bracketleft"),
        [0xDD] = new(0x005D, "bracketright"),
        [0xDC] = new(0x005C, "backslash"),
        [0xBA] = new(0x003B, "semicolon"),
        [0xDE] = new(0x0027, "apostrophe"),
        [0xBC] = new(0x002C, "comma"),
        [0xBE] = new(0x002E, "period"),
        [0xBF] = new(0x002F, "slash"),
        [0x2D] = new(0xFF63, "Insert"),
        [0x2E] = new(0xFFFF, "Delete"),
        [0x24] = new(0xFF50, "Home"),
        [0x23] = new(0xFF57, "End"),
        [0x21] = new(0xFF55, "Page_Up"),
        [0x22] = new(0xFF56, "Page_Down"),
        [0x26] = new(0xFF52, "Up"),
        [0x28] = new(0xFF54, "Down"),
        [0x25] = new(0xFF51, "Left"),
        [0x27] = new(0xFF53, "Right"),
    };

    internal static bool TryFromVirtualKey(uint virtualKey, out KeySymbol symbol)
    {
        switch (virtualKey)
        {
            case >= 'A' and <= 'Z':
                var letter = (char)(virtualKey + ('a' - 'A'));
                symbol = new KeySymbol(letter, letter.ToString());
                return true;
            case >= '0' and <= '9':
                symbol = new KeySymbol(virtualKey, ((char)virtualKey).ToString());
                return true;
            case >= 0x70 and <= 0x87:
                var function = virtualKey - 0x70 + 1;
                symbol = new KeySymbol(0xFFBE + function - 1, $"F{function}");
                return true;
            case >= 0x60 and <= 0x69:
                var digit = virtualKey - 0x60;
                symbol = new KeySymbol(0xFFB0 + digit, $"KP_{digit}");
                return true;
        }

        return Named.TryGetValue(virtualKey, out symbol);
    }

    internal static IReadOnlyList<LinuxHotkey> Resolve(IReadOnlyDictionary<HotkeyAction, HotkeyBinding?> effective)
    {
        var hotkeys = new List<LinuxHotkey>();
        foreach (var (action, binding) in effective.OrderBy(pair => pair.Key))
        {
            if (binding is null || string.IsNullOrEmpty(binding.Key))
                continue;

            if (!HotkeyBindingFormat.TryParse(binding, out var modifiers, out var virtualKey)
                || !TryFromVirtualKey(virtualKey, out var symbol))
            {
                continue;
            }

            hotkeys.Add(new LinuxHotkey(action, modifiers, symbol));
        }

        return hotkeys;
    }

    internal static string PortalTrigger(LinuxHotkey hotkey)
    {
        var parts = new List<string>(5);
        if ((hotkey.Modifiers & HotkeyBindingFormat.ModControl) != 0)
            parts.Add("CTRL");
        if ((hotkey.Modifiers & HotkeyBindingFormat.ModAlt) != 0)
            parts.Add("ALT");
        if ((hotkey.Modifiers & HotkeyBindingFormat.ModShift) != 0)
            parts.Add("SHIFT");
        if ((hotkey.Modifiers & HotkeyBindingFormat.ModWin) != 0)
            parts.Add("LOGO");
        parts.Add(hotkey.Key.Name);
        return string.Join('+', parts);
    }
}
