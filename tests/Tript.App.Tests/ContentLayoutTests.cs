// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.App.Content;
using Xunit;

namespace Tript.App.Tests;

public sealed class ContentLayoutTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tript-layout-root"));

    [Theory]
    [InlineData("sessions/a.mp4", "sessions")]
    [InlineData("Valorant/sessions/a.mp4", "sessions")]
    [InlineData("Valorant/highlights/a.mp4", "highlights")]
    [InlineData("Valorant/clips/a.mp4", "clips")]
    [InlineData("loose.mp4", "loose.mp4")]
    [InlineData("other/a.mp4", "other")]
    public void TopLevelDirectory_FindsTheContentFolder(string wirePath, string expected)
    {
        Assert.Equal(expected, ContentLayout.TopLevelDirectory(wirePath));
    }

    [Theory]
    [InlineData("Valorant/sessions/a.mp4", "Valorant")]
    [InlineData("sessions/a.mp4", null)]
    [InlineData(".trash/x/a.mp4", null)]
    [InlineData("metadata/a.json", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void GameSegment_IsTheFirstFolderUnlessItIsALibraryFolder(string? wirePath, string? expected)
    {
        Assert.Equal(expected, ContentLayout.GameSegment(wirePath));
    }

    [Fact]
    public void SiblingOfSessions_KeepsTheGameFolder()
    {
        var source = Path.Combine(Root, "Valorant", "sessions", "a.mp4");

        Assert.Equal(Path.Combine(Root, "Valorant", "clips"),
            ContentLayout.SiblingOfSessions(Root, source, ContentLayout.Clips));
        Assert.Equal(Path.Combine(Root, "Valorant", "highlights"),
            ContentLayout.SiblingOfSessions(Root, source, ContentLayout.Highlights));
    }

    [Fact]
    public void SiblingOfSessions_FallsBackToTheRootFolder()
    {
        Assert.Equal(Path.Combine(Root, "clips"),
            ContentLayout.SiblingOfSessions(Root, Path.Combine(Root, "sessions", "a.mp4"), ContentLayout.Clips));
        Assert.Equal(Path.Combine(Root, "highlights"),
            ContentLayout.SiblingOfSessions(Root, Path.Combine(Root, "loose.mp4"), ContentLayout.Highlights));
    }

    [Fact]
    public void WirePaths_UseForwardSlashes()
    {
        var wire = ContentLayout.ToWirePath(Root, Path.Combine(Root, "Valorant", "sessions", "a.mp4"));

        Assert.Equal("Valorant/sessions/a.mp4", wire);
        Assert.Equal("a.mp4", ContentLayout.FileNameOf(wire));
        Assert.True(ContentLayout.IsSessionPath(wire));
        Assert.False(ContentLayout.IsClipPath(wire));
        Assert.True(ContentLayout.IsTrashPath(".trash/entry/files/a.mp4"));
    }
}
