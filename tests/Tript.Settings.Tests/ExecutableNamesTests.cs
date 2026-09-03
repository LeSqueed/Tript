// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Core;
using Xunit;

namespace Tript.Settings.Tests;

public sealed class ExecutableNamesTests
{
    [Theory]
    [InlineData("ALPHA.EXE", "ALPHA")]
    [InlineData(" alpha.exe ", "alpha")]
    [InlineData("alpha", "alpha")]
    [InlineData(@"C:\Games\Doom\doom.exe", "doom")]
    [InlineData("/opt/games/doom", "doom")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_KeepsOnlyTheBaseName(string? input, string expected)
        => Assert.Equal(expected, ExecutableNames.Normalize(input));

    [Fact]
    public void Comparer_IgnoresCaseOnEveryHost()
    {
        Assert.True(ExecutableNames.Comparer.Equals("ALPHA", "alpha"));
        Assert.True(ExecutableNames.Equal(@"C:\Games\ALPHA.EXE", "alpha"));
        Assert.False(ExecutableNames.Equal("alpha", "beta"));
    }

    [Theory]
    [InlineData(@"C:\Games\game.exe", true)]
    [InlineData("game.EXE", true)]
    [InlineData(@"C:\dir.exe\game", false)]
    [InlineData("game", false)]
    public void HasExeExtension_LooksAtTheFileNameOnly(string path, bool expected)
        => Assert.Equal(expected, ExecutableNames.HasExeExtension(path));
}
