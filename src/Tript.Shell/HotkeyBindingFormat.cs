// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;

namespace Tript.Shell;

// Translates a HotkeyBinding (modifier names + a KeyboardEvent.code string, as captured by the
// Settings UI) into the Win32 MOD_*/VK_* values RegisterHotKey needs. Kept separate from
// WindowsHotkeys so the mapping table can grow without touching the registration/message-loop code.
internal static class HotkeyBindingFormat
{
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    internal static bool TryParse(HotkeyBinding binding, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;

        if (string.IsNullOrWhiteSpace(binding.Key))
            return false;

        foreach (var modifier in binding.Modifiers)
        {
            modifiers |= modifier.Trim().ToLowerInvariant() switch
            {
                "control" => ModControl,
                "shift" => ModShift,
                "alt" => ModAlt,
                "win" => ModWin,
                _ => 0u,
            };
        }

        if (!TryMapKeyCode(binding.Key, out virtualKey))
            return false;

        return true;
    }

    private static bool TryMapKeyCode(string code, out uint virtualKey)
    {
        virtualKey = 0;

        if (code.Length == 4 && code.StartsWith("Key", StringComparison.Ordinal)
            && code[3] is >= 'A' and <= 'Z')
        {
            virtualKey = code[3];
            return true;
        }

        if (code.Length == 6 && code.StartsWith("Digit", StringComparison.Ordinal)
            && code[5] is >= '0' and <= '9')
        {
            virtualKey = code[5];
            return true;
        }

        if (code.Length is 2 or 3 && code[0] == 'F' && int.TryParse(code.AsSpan(1), out var fKey)
            && fKey is >= 1 and <= 24)
        {
            virtualKey = 0x6Fu + (uint)fKey; // VK_F1 = 0x70
            return true;
        }

        return code switch
        {
            "Space" => Set(out virtualKey, 0x20),
            "Tab" => Set(out virtualKey, 0x09),
            "Backquote" => Set(out virtualKey, 0xC0),
            "Minus" => Set(out virtualKey, 0xBD),
            "Equal" => Set(out virtualKey, 0xBB),
            "BracketLeft" => Set(out virtualKey, 0xDB),
            "BracketRight" => Set(out virtualKey, 0xDD),
            "Backslash" => Set(out virtualKey, 0xDC),
            "Semicolon" => Set(out virtualKey, 0xBA),
            "Quote" => Set(out virtualKey, 0xDE),
            "Comma" => Set(out virtualKey, 0xBC),
            "Period" => Set(out virtualKey, 0xBE),
            "Slash" => Set(out virtualKey, 0xBF),
            "Insert" => Set(out virtualKey, 0x2D),
            "Delete" => Set(out virtualKey, 0x2E),
            "Home" => Set(out virtualKey, 0x24),
            "End" => Set(out virtualKey, 0x23),
            "PageUp" => Set(out virtualKey, 0x21),
            "PageDown" => Set(out virtualKey, 0x22),
            "ArrowUp" => Set(out virtualKey, 0x26),
            "ArrowDown" => Set(out virtualKey, 0x28),
            "ArrowLeft" => Set(out virtualKey, 0x25),
            "ArrowRight" => Set(out virtualKey, 0x27),
            "Numpad0" => Set(out virtualKey, 0x60),
            "Numpad1" => Set(out virtualKey, 0x61),
            "Numpad2" => Set(out virtualKey, 0x62),
            "Numpad3" => Set(out virtualKey, 0x63),
            "Numpad4" => Set(out virtualKey, 0x64),
            "Numpad5" => Set(out virtualKey, 0x65),
            "Numpad6" => Set(out virtualKey, 0x66),
            "Numpad7" => Set(out virtualKey, 0x67),
            "Numpad8" => Set(out virtualKey, 0x68),
            "Numpad9" => Set(out virtualKey, 0x69),
            _ => false,
        };
    }

    private static bool Set(out uint virtualKey, uint value)
    {
        virtualKey = value;
        return true;
    }
}
