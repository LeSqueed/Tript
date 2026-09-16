// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.App.Tests;

public sealed class GameFolderNameTests
{
    [Theory]
    [InlineData("Valorant", "Valorant")]
    [InlineData("  Spaced  ", "Spaced")]
    [InlineData("a:b", "a_b")]
    [InlineData("CON", "_CON")]
    [InlineData("nul", "_nul")]
    [InlineData("Com1", "_Com1")]
    [InlineData("LPT9.game", "_LPT9.game")]
    [InlineData("COM0", "COM0")]
    [InlineData("Console", "Console")]
    [InlineData("Game...", "Game")]
    [InlineData("...", "Unknown Game")]
    [InlineData("..", "Unknown Game")]
    [InlineData("   ", "Unknown Game")]
    public void WindowsRules_ProduceAFolderWindowsCanCreate(string value, string expected)
    {
        Assert.Equal(expected, AppHost.SafeDirectoryName(value, windowsRules: true));
    }

    [Theory]
    [InlineData("CON", "CON")]
    [InlineData("Game.", "Game.")]
    [InlineData("..", "Unknown Game")]
    public void OtherPlatforms_KeepNamesWindowsWouldRefuse(string value, string expected)
    {
        Assert.Equal(expected, AppHost.SafeDirectoryName(value, windowsRules: false));
    }
}
