// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.App.Updater;
using Xunit;

namespace Tript.App.Tests;

public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("0.1.0-alpha.2")]
    [InlineData("v0.1.0-alpha.2")]
    [InlineData("V0.1.0-alpha.2")]
    [InlineData("1.0.0")]
    [InlineData("v1.0.0")]
    [InlineData("0.1.0-alpha.2+abc123")]
    public void TryParse_AcceptsValidVersions(string raw)
    {
        Assert.True(ReleaseVersion.TryParse(raw, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-version")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-alpha")]
    [InlineData("1.0.0-alpha.x")]
    [InlineData("1.0.0-2.alpha")]
    [InlineData("-1.0.0")]
    public void TryParse_RejectsInvalidVersions(string raw)
    {
        Assert.False(ReleaseVersion.TryParse(raw, out _));
    }

    [Fact]
    public void IsNewer_OrdersPrereleaseNumbersWithinTheSameLabel()
    {
        Assert.True(ReleaseVersion.IsNewer("v0.1.0-alpha.3", "0.1.0-alpha.2"));
        Assert.False(ReleaseVersion.IsNewer("v0.1.0-alpha.2", "0.1.0-alpha.3"));
    }

    [Fact]
    public void IsNewer_OrdersBetaAboveAlpha()
    {
        Assert.True(ReleaseVersion.IsNewer("v0.1.0-beta.1", "0.1.0-alpha.9"));
        Assert.False(ReleaseVersion.IsNewer("v0.1.0-alpha.9", "0.1.0-beta.1"));
    }

    [Fact]
    public void IsNewer_StableBeatsAnyPrereleaseOfTheSameCoreVersion()
    {
        Assert.True(ReleaseVersion.IsNewer("v1.0.0", "1.0.0-beta.9"));
        Assert.False(ReleaseVersion.IsNewer("v1.0.0-beta.9", "1.0.0"));
    }

    [Fact]
    public void IsNewer_ComparesCoreVersionBeforePrereleaseLabel()
    {
        Assert.True(ReleaseVersion.IsNewer("v0.2.0-alpha.1", "0.1.0-beta.9"));
    }

    [Fact]
    public void IsNewer_EqualVersionsAreNotNewer()
    {
        Assert.False(ReleaseVersion.IsNewer("v0.1.0-alpha.2", "0.1.0-alpha.2"));
    }

    [Fact]
    public void IsNewer_UnparseableInputNeverWins()
    {
        Assert.False(ReleaseVersion.IsNewer("not-a-version", "0.1.0-alpha.1"));
        Assert.False(ReleaseVersion.IsNewer("0.1.0-alpha.2", "not-a-version"));
        Assert.False(ReleaseVersion.IsNewer("not-a-version", "also-not-a-version"));
    }
}
