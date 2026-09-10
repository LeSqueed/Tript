// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;
using Xunit;

namespace Tript.Recorder.Tests;

public sealed class DisplayColourResolverTests
{
    private static ObsRuntime.DisplayColour D(int i, ObsSourceColorSpace space, float nits) => new(i, space, nits);

    [Fact]
    public void NoProbes_YieldsNothing()
    {
        var choice = DisplayColourResolver.Choose([]);

        Assert.Null(choice.ColourSpace);
        Assert.Equal(0f, choice.SdrWhiteLevelNits);
    }

    [Fact]
    public void AllSdr_ReportsTheFirstSpaceAndTheFirstRealWhiteLevel()
    {
        var choice = DisplayColourResolver.Choose(
            [D(0, ObsSourceColorSpace.Srgb, 0f), D(1, ObsSourceColorSpace.Srgb, 200f)]);

        Assert.Equal(ObsSourceColorSpace.Srgb, choice.ColourSpace);
        Assert.Equal(200f, choice.SdrWhiteLevelNits);
    }

    [Theory]
    [InlineData(ObsSourceColorSpace.Extended709)]
    [InlineData(ObsSourceColorSpace.Scrgb709)]
    public void AnHdrMonitorAnywhere_IsReported_WithItsOwnWhiteLevel(ObsSourceColorSpace hdrSpace)
    {
        var choice = DisplayColourResolver.Choose(
        [
            D(0, ObsSourceColorSpace.Srgb, 80f),
            D(1, hdrSpace, 240f),
            D(2, ObsSourceColorSpace.Srgb, 80f),
        ]);

        Assert.Equal(hdrSpace, choice.ColourSpace);
        Assert.Equal(240f, choice.SdrWhiteLevelNits);
    }

    [Fact]
    public void AnHdrMonitorPastTheSdrOnes_StillWins()
    {
        var choice = DisplayColourResolver.Choose(
        [
            D(0, ObsSourceColorSpace.Srgb, 80f),
            D(1, ObsSourceColorSpace.Srgb, 80f),
            D(2, ObsSourceColorSpace.Scrgb709, 200f),
        ]);

        Assert.Equal(ObsSourceColorSpace.Scrgb709, choice.ColourSpace);
        Assert.Equal(200f, choice.SdrWhiteLevelNits);
    }

    [Fact]
    public void HighPrecisionSdr_IsNotTreatedAsHdr()
    {
        var choice = DisplayColourResolver.Choose(
            [D(0, ObsSourceColorSpace.Srgb16F, 120f)]);

        Assert.Equal(ObsSourceColorSpace.Srgb16F, choice.ColourSpace);
        Assert.Equal(120f, choice.SdrWhiteLevelNits);
    }

    [Fact]
    public void AnHdrMonitorWithNoWhiteLevel_DoesNotBorrowAnSdrOnes()
    {
        var choice = DisplayColourResolver.Choose(
            [D(0, ObsSourceColorSpace.Srgb, 200f), D(1, ObsSourceColorSpace.Scrgb709, 0f)]);

        Assert.Equal(ObsSourceColorSpace.Scrgb709, choice.ColourSpace);
        Assert.Equal(0f, choice.SdrWhiteLevelNits);
    }
}
