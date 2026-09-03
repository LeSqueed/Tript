// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Core;
using Xunit;

namespace Tript.Settings.Tests;

public sealed class FilePathsTests
{
    [Theory]
    [InlineData(@"C:\Games\Doom\doom.exe", "doom.exe")]
    [InlineData("/opt/games/doom", "doom")]
    [InlineData(@"bin/sub\game.exe", "game.exe")]
    [InlineData("game.exe", "game.exe")]
    public void FileName_SplitsOnEitherSeparator(string path, string expected)
        => Assert.Equal(expected, FilePaths.FileName(path));

    [Theory]
    [InlineData(@"C:\Games\a.exe", true)]
    [InlineData("D:/Games/a.exe", true)]
    [InlineData(@"bin\a.exe", false)]
    [InlineData("a.exe", false)]
    public void IsFullyQualified_AcceptsDriveRootsOnEveryHost(string path, bool expected)
        => Assert.Equal(expected, FilePaths.IsFullyQualified(path));

    [Fact]
    public void IsFullyQualified_AcceptsNativeAbsolutePaths()
        => Assert.True(FilePaths.IsFullyQualified(Path.GetTempPath()));

    [Fact]
    public void ToNativeSeparators_TranslatesBothStyles()
    {
        var expected = string.Join(Path.DirectorySeparatorChar, "bin", "sub", "game.exe");
        Assert.Equal(expected, FilePaths.ToNativeSeparators(@"bin\sub/game.exe"));
    }

    [Fact]
    public void ContainsSeparator_SeesBackslashOnEveryHost()
    {
        Assert.True(FilePaths.ContainsSeparator(@"bin\game.exe"));
        Assert.True(FilePaths.ContainsSeparator("bin/game.exe"));
        Assert.False(FilePaths.ContainsSeparator("game.exe"));
    }

    [Fact]
    public void IsUnder_RequiresAWholeSegmentPrefix()
    {
        var root = Path.Combine(Path.GetTempPath(), "tript-filepaths-root");
        Assert.True(FilePaths.IsUnder(Path.Combine(root, "bin", "game.exe"), root));
        Assert.True(FilePaths.IsUnder(Path.Combine(root, "bin", "game.exe"), root + Path.DirectorySeparatorChar));
        Assert.False(FilePaths.IsUnder(root + "-sibling" + Path.DirectorySeparatorChar + "game.exe", root));
        Assert.False(FilePaths.IsUnder(root, root));
        Assert.False(FilePaths.IsUnder(Path.Combine(root, "game.exe"), string.Empty));
    }

    [Fact]
    public void IsUnder_CollapsesTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "tript-filepaths-root");
        var escaped = Path.Combine(root, FilePaths.ToNativeSeparators(@"bin\..\..\outside.exe"));
        Assert.False(FilePaths.IsUnder(escaped, root));
    }

    [Fact]
    public void IsAtOrUnder_AcceptsTheRootItself()
    {
        var root = Path.Combine(Path.GetTempPath(), "tript-filepaths-root");
        Assert.True(FilePaths.IsAtOrUnder(root, root));
        Assert.True(FilePaths.IsAtOrUnder(root + Path.DirectorySeparatorChar, root));
        Assert.True(FilePaths.IsAtOrUnder(Path.Combine(root, "x"), root));
        Assert.False(FilePaths.IsAtOrUnder(Path.GetTempPath(), root));
    }

    [Fact]
    public void ContainmentChecks_RejectInvalidPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "tript-filepaths-root");
        const string invalidPath = "invalid\0path";

        Assert.False(FilePaths.IsUnder(invalidPath, root));
        Assert.False(FilePaths.IsAtOrUnder(invalidPath, root));
    }

    [Fact]
    public void TryGetFullPath_IsNullForBlankInput()
    {
        Assert.Null(FilePaths.TryGetFullPath(null));
        Assert.Null(FilePaths.TryGetFullPath("  "));
        Assert.Equal(Path.GetFullPath("game.exe"), FilePaths.TryGetFullPath(" game.exe "));
    }

    [Fact]
    public void Comparer_FollowsTheHost()
    {
        var equal = FilePaths.Comparer.Equals("Game.exe", "game.exe");
        Assert.Equal(OperatingSystem.IsWindows(), equal);
        Assert.Equal(OperatingSystem.IsWindows() ? "GAME.EXE" : "game.exe", FilePaths.CaseFold("game.exe"));
    }
}
