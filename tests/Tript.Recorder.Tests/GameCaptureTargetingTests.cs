// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;
using Xunit;

namespace Tript.Recorder.Tests;

public sealed class GameCaptureTargetingTests
{
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

    [Fact]
    public void ColonsAndHashesInATarget_AreEscapedTheWayTheDecodeExpects()
    {
        var window = ObsCaptureSource.BuildWindowMatchString(
            new ObsGameCaptureTarget("Half-Life 2: Episode #1", null, "hl2.exe"));

        Assert.Equal("Half-Life 2#3A Episode #221:*:hl2.exe", window);
        Assert.Equal(3, window.Split(':').Length);
    }

    [Fact]
    public void AnEmptyTarget_IsReportedEmptyRatherThanBecomingAWildcardMatch()
    {
        Assert.True(new ObsGameCaptureTarget(null, null, null).IsEmpty);
        Assert.False(new ObsGameCaptureTarget(null, null, "game.exe").IsEmpty);
    }

    [Fact]
    public void BuildWindowMatchString_RejectsANullTarget() =>
        Assert.Throws<ArgumentNullException>(() => ObsCaptureSource.BuildWindowMatchString(null!));

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

    [Fact]
    public void TheMatchPriorityValues_AreTheOnesWinCaptureNumbers()
    {
        Assert.Equal(0, (int)WindowPriority.Title);
        Assert.Equal(1, (int)WindowPriority.Class);
        Assert.Equal(2, (int)WindowPriority.Exe);
    }

    [Fact]
    public void TheCaptureMode_IsTakenFromTheCaptureModePropertysOwnItems()
    {
        var properties = GameCaptureProperties();

        Assert.Equal("window", ObsRecorderSession.ResolveWindowCaptureMode(properties));
    }

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
