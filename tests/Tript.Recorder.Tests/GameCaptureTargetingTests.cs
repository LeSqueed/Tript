// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;
using Xunit;

namespace Tript.Recorder.Tests;

// The rules that decide whether win-capture's game_capture ever attaches to anything. None of them
// is visible at run time on Linux — there is no game_capture there — and all of them are silent
// when wrong: the source is created, the recording starts, the audio is fine and the picture is a
// black rectangle.
public sealed class GameCaptureTargetingTests
{
    // The bug this pins. win-capture splits the "window" setting into title:class:exe, decodes an
    // empty part to NULL, and ms_find_window returns NULL immediately when the class is NULL — so a
    // "::game.exe" string matches no window at all however right the executable is.
    [Fact]
    public void AnExeOnlyTarget_NeverLeavesTheClassOrTitleEmpty()
    {
        var window = ObsCaptureSource.BuildWindowMatchString(new ObsGameCaptureTarget(null, null, "Overwatch.exe"));

        Assert.Equal("*:*:Overwatch.exe", window);

        var parts = window.Split(':');
        Assert.Equal(3, parts.Length);
        Assert.All(parts, part => Assert.NotEqual(string.Empty, part));
    }

    [Fact]
    public void AFullyKnownTarget_KeepsEveryPartInTitleClassExeOrder()
    {
        var window = ObsCaptureSource.BuildWindowMatchString(
            new ObsGameCaptureTarget("Overwatch", "TankWindowClass", "Overwatch.exe"));

        Assert.Equal("Overwatch:TankWindowClass:Overwatch.exe", window);
    }

    [Fact]
    public void APartlyKnownTarget_FillsOnlyTheMissingPartsWithTheWildcard()
    {
        Assert.Equal("Overwatch:*:*", ObsCaptureSource.BuildWindowMatchString(
            new ObsGameCaptureTarget("Overwatch", null, null)));

        Assert.Equal("*:TankWindowClass:*", ObsCaptureSource.BuildWindowMatchString(
            new ObsGameCaptureTarget(null, "TankWindowClass", null)));
    }

    // The separator has to survive: a title with a colon in it would otherwise split into four
    // parts and the plugin would read the wrong field as the executable. '#' is escaped first
    // because the plugin's decode replaces "#3A" before "#22".
    [Fact]
    public void ColonsAndHashesInATarget_AreEscapedTheWayTheDecodeExpects()
    {
        var window = ObsCaptureSource.BuildWindowMatchString(
            new ObsGameCaptureTarget("Half-Life 2: Episode #1", null, "hl2.exe"));

        Assert.Equal("Half-Life 2#3A Episode #221:*:hl2.exe", window);
        Assert.Equal(3, window.Split(':').Length);
    }

    // An empty target is not a target: writing "*:*:*" would tell the plugin to hook the first
    // window it can find.
    [Fact]
    public void AnEmptyTarget_IsReportedEmptyRatherThanBecomingAWildcardMatch()
    {
        Assert.True(new ObsGameCaptureTarget(null, null, null).IsEmpty);
        Assert.False(new ObsGameCaptureTarget(null, null, "game.exe").IsEmpty);
    }

    [Fact]
    public void BuildWindowMatchString_RejectsANullTarget() =>
        Assert.Throws<ArgumentNullException>(() => ObsCaptureSource.BuildWindowMatchString(null!));

    // The executable is the identity the game detector actually has and the only part that survives
    // a game re-creating its window, so it decides the match whenever it is known.
    // The expectation is the raw window_priority number, because that is what the plugin compares.
    [Theory]
    [InlineData(null, null, "game.exe", 2)]
    [InlineData("Game", null, "game.exe", 2)]
    [InlineData("Game", "GameClass", "game.exe", 2)]
    [InlineData("Game", null, null, 0)]
    [InlineData(null, "GameClass", null, 1)]
    public void TheMatchPriority_FollowsTheMostStableKnownPart(
        string? title, string? windowClass, string? executable, int expected) =>
        Assert.Equal(
            expected,
            (int)ObsCaptureSource.ResolveWindowPriority(new ObsGameCaptureTarget(title, windowClass, executable)));

    // The values are win-capture's window_priority enum, compared by number in the plugin.
    [Fact]
    public void TheMatchPriorityValues_AreTheOnesWinCaptureNumbers()
    {
        Assert.Equal(0, (int)WindowPriority.Title);
        Assert.Equal(1, (int)WindowPriority.Class);
        Assert.Equal(2, (int)WindowPriority.Exe);
    }

    // ---- capture_mode ----

    // Left unset, capture_mode defaults to "any_fullscreen", which hooks whichever fullscreen
    // window is in the foreground and ignores the window string entirely.
    [Fact]
    public void TheCaptureMode_IsTakenFromTheCaptureModePropertysOwnItems()
    {
        var properties = GameCaptureProperties();

        Assert.Equal("window", ObsRecorderSession.ResolveWindowCaptureMode(properties));
    }

    // The window *list* also carries items spelled "window"; picking the first list that does would
    // write the mode into the window selection instead.
    [Fact]
    public void TheCaptureMode_IsNotTakenFromAnotherListThatHappensToMentionWindow()
    {
        var properties = new List<ObsSourceProperty>
        {
            new("window", ObsPropertyType.List,
            [
                new ObsSourcePropertyItem("Some Game:UnityWndClass:game.exe", ObsComboFormat.String),
                new ObsSourcePropertyItem("any window at all", ObsComboFormat.String)
            ]),
            new("capture_mode", ObsPropertyType.List,
            [
                new ObsSourcePropertyItem("any_fullscreen", ObsComboFormat.String),
                new ObsSourcePropertyItem("window", ObsComboFormat.String),
                new ObsSourcePropertyItem("hotkey", ObsComboFormat.String)
            ])
        };

        Assert.Equal("window", ObsRecorderSession.ResolveWindowCaptureMode(properties));
    }

    // A plugin build that declares no capture_mode is reported rather than guessed at, so the
    // caller can say so instead of silently recording in the wrong mode.
    [Fact]
    public void APropertySetWithoutTheCaptureMode_ResolvesToNothing()
    {
        var properties = new List<ObsSourceProperty>
        {
            new("window", ObsPropertyType.List, []),
            new("priority", ObsPropertyType.List, [])
        };

        Assert.Null(ObsRecorderSession.ResolveWindowCaptureMode(properties));
        Assert.Null(ObsRecorderSession.ResolveWindowCaptureMode([]));
    }

    [Fact]
    public void ResolveWindowCaptureMode_RejectsANullPropertyList() =>
        Assert.Throws<ArgumentNullException>(() => ObsRecorderSession.ResolveWindowCaptureMode(null!));

    // The property set win-capture 32 declares, in its own order.
    private static IReadOnlyList<ObsSourceProperty> GameCaptureProperties() =>
    [
        new("capture_mode", ObsPropertyType.List,
        [
            new ObsSourcePropertyItem("any_fullscreen", ObsComboFormat.String),
            new ObsSourcePropertyItem("window", ObsComboFormat.String),
            new ObsSourcePropertyItem("hotkey", ObsComboFormat.String)
        ]),
        new("window", ObsPropertyType.List,
        [
            new ObsSourcePropertyItem(string.Empty, ObsComboFormat.String)
        ]),
        new("priority", ObsPropertyType.List,
        [
            new ObsSourcePropertyItem(0L, ObsComboFormat.Int),
            new ObsSourcePropertyItem(1L, ObsComboFormat.Int),
            new ObsSourcePropertyItem(2L, ObsComboFormat.Int)
        ]),
        new("capture_cursor", ObsPropertyType.Bool, []),
        new("hook_rate", ObsPropertyType.List, [])
    ];
}
