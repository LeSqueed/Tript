// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Core;
using Xunit;

namespace Tript.App.Tests;

public sealed class AutomaticClipCandidatesTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, false)]
    public void Play_FollowsItsExplicitFlag(bool? flag, bool expected)
    {
        var bookmark = new Bookmark { Type = BookmarkType.Play, IsAutomaticClipCandidate = flag };

        Assert.Equal(expected, AutomaticClipCandidates.Includes(bookmark));
    }

    [Fact]
    public void Kill_WithoutAFlag_StillFallsBackToItsType()
    {
        Assert.True(AutomaticClipCandidates.Includes(new Bookmark { Type = BookmarkType.Kill }));
    }
}
