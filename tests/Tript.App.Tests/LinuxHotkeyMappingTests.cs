// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;
using Tript.Shell;
using Tript.Shell.Linux;
using Xunit;

namespace Tript.App.Tests;

public sealed class LinuxHotkeyMappingTests
{
    private static IReadOnlyDictionary<HotkeyAction, HotkeyBinding?> Bindings(
        params (HotkeyAction Action, string[] Modifiers, string? Key)[] bindings) =>
        bindings.ToDictionary(binding => binding.Action,
            binding => (HotkeyBinding?)new HotkeyBinding { Modifiers = [.. binding.Modifiers], Key = binding.Key });

    [Theory]
    [InlineData(new[] { "Control", "Shift" }, "F9", "CTRL+SHIFT+F9")]
    [InlineData(new[] { "Alt" }, "KeyB", "ALT+b")]
    [InlineData(new[] { "Win", "Control" }, "Digit3", "CTRL+LOGO+3")]
    [InlineData(new[] { "Control", "Alt", "Shift", "Win" }, "PageDown", "CTRL+ALT+SHIFT+LOGO+Page_Down")]
    [InlineData(new[] { "Control" }, "Numpad7", "CTRL+KP_7")]
    [InlineData(new[] { "Control" }, "Backquote", "CTRL+grave")]
    public void ABinding_BecomesTheXdgShortcutTrigger(string[] modifiers, string key, string trigger)
    {
        var hotkey = Assert.Single(LinuxKeySymbols.Resolve(Bindings((HotkeyAction.QuickClip, modifiers, key))));

        Assert.Equal(trigger, LinuxKeySymbols.PortalTrigger(hotkey));
    }

    [Theory]
    [InlineData("KeyA", 0x61u)]
    [InlineData("KeyZ", 0x7Au)]
    [InlineData("Digit0", 0x30u)]
    [InlineData("F1", 0xFFBEu)]
    [InlineData("F24", 0xFFD5u)]
    [InlineData("Space", 0x20u)]
    [InlineData("Tab", 0xFF09u)]
    [InlineData("Delete", 0xFFFFu)]
    [InlineData("ArrowLeft", 0xFF51u)]
    [InlineData("Numpad0", 0xFFB0u)]
    [InlineData("Quote", 0x27u)]
    public void AKeyCode_MapsToItsX11KeySymbol(string code, uint keysym)
    {
        var hotkey = Assert.Single(LinuxKeySymbols.Resolve(Bindings((HotkeyAction.ToggleRecording, ["Control"], code))));

        Assert.Equal(keysym, hotkey.Key.Value);
    }

    [Fact]
    public void EveryKeyTheBindingFormatAccepts_HasALinuxKeySymbol()
    {
        string[] codes =
        [
            .. Enumerable.Range('A', 26).Select(letter => $"Key{(char)letter}"),
            .. Enumerable.Range(0, 10).Select(digit => $"Digit{digit}"),
            .. Enumerable.Range(1, 24).Select(function => $"F{function}"),
            .. Enumerable.Range(0, 10).Select(digit => $"Numpad{digit}"),
            "Space", "Tab", "Backquote", "Minus", "Equal", "BracketLeft", "BracketRight", "Backslash", "Semicolon",
            "Quote", "Comma", "Period", "Slash", "Insert", "Delete", "Home", "End", "PageUp", "PageDown", "ArrowUp",
            "ArrowDown", "ArrowLeft", "ArrowRight",
        ];

        foreach (var code in codes)
        {
            Assert.True(HotkeyBindingFormat.TryParse(new HotkeyBinding { Key = code }, out _, out var virtualKey), code);
            Assert.True(LinuxKeySymbols.TryFromVirtualKey(virtualKey, out _), code);
        }
    }

    [Fact]
    public void UnboundAndUnknownKeys_AreLeftOut()
    {
        var resolved = LinuxKeySymbols.Resolve(new Dictionary<HotkeyAction, HotkeyBinding?>
        {
            [HotkeyAction.ToggleRecording] = null,
            [HotkeyAction.ManualBookmark] = new HotkeyBinding { Modifiers = ["Control"], Key = "" },
            [HotkeyAction.QuickClip] = new HotkeyBinding { Modifiers = ["Control"], Key = "MediaPlayPause" },
        });

        Assert.Empty(resolved);
    }

    [Fact]
    public void ResolvedHotkeys_AreOrderedByAction()
    {
        var resolved = LinuxKeySymbols.Resolve(Bindings(
            (HotkeyAction.QuickClip, ["Control"], "KeyC"),
            (HotkeyAction.ToggleRecording, ["Control"], "KeyR")));

        Assert.Equal([HotkeyAction.ToggleRecording, HotkeyAction.QuickClip], resolved.Select(hotkey => hotkey.Action));
    }

    [Fact]
    public void PortalShortcuts_HaveStableIdsAndReadableDescriptions()
    {
        var shortcuts = PortalGlobalShortcuts.Describe(LinuxKeySymbols.Resolve(Bindings(
            (HotkeyAction.ToggleRecording, ["Control"], "KeyR"),
            (HotkeyAction.ManualBookmark, ["Control"], "KeyB"),
            (HotkeyAction.QuickClip, ["Control"], "KeyC"))));

        Assert.Equal(["toggle-recording", "manual-bookmark", "quick-clip"], shortcuts.Select(shortcut => shortcut.Id));
        Assert.All(shortcuts, shortcut => Assert.False(string.IsNullOrWhiteSpace(shortcut.Description)));
    }

    [Fact]
    public void BindingModifiers_BecomeX11ModifierMasks()
    {
        Assert.Equal(X11KeyGrabs.ControlMask | X11KeyGrabs.Mod1Mask,
            X11KeyGrabs.FromBindingModifiers(HotkeyBindingFormat.ModControl | HotkeyBindingFormat.ModAlt));
        Assert.Equal(X11KeyGrabs.ShiftMask | X11KeyGrabs.Mod4Mask,
            X11KeyGrabs.FromBindingModifiers(HotkeyBindingFormat.ModShift | HotkeyBindingFormat.ModWin));
    }

    [Fact]
    public void AnX11Grab_CoversNumLockAndCapsLockVariants()
    {
        var masks = X11KeyGrabs.GrabMasks(X11KeyGrabs.ControlMask).ToList();

        Assert.Equal(4, masks.Distinct().Count());
        Assert.Contains(X11KeyGrabs.ControlMask, masks);
        Assert.Contains(X11KeyGrabs.ControlMask | X11KeyGrabs.LockMask, masks);
        Assert.Contains(X11KeyGrabs.ControlMask | X11KeyGrabs.Mod2Mask, masks);
        Assert.Contains(X11KeyGrabs.ControlMask | X11KeyGrabs.LockMask | X11KeyGrabs.Mod2Mask, masks);
    }

    [Fact]
    public void AKeyPressWithNumLockOn_MatchesTheGrabWithoutIt() =>
        Assert.Equal(X11KeyGrabs.ControlMask | X11KeyGrabs.ShiftMask,
            X11KeyGrabs.Significant(X11KeyGrabs.ControlMask | X11KeyGrabs.ShiftMask | X11KeyGrabs.Mod2Mask
                | X11KeyGrabs.LockMask));

    [Fact]
    public void AHeldKey_FiresOnceUntilItIsReleased()
    {
        var repeats = new X11RepeatFilter();

        Assert.True(repeats.Pressed(42));
        Assert.False(repeats.Pressed(42));
        Assert.True(repeats.Pressed(43));
        repeats.Released(42);
        Assert.True(repeats.Pressed(42));
    }
}
