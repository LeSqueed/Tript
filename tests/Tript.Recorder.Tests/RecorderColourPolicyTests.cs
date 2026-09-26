// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;
using Xunit;

namespace Tript.Recorder.Tests;

public sealed class RecorderColourPolicyTests
{
    [Fact]
    public void TheDisplayProbe_DecidesWhenItHasAnAnswer()
    {
        var space = RecorderColourPolicy.CapturedColourSpace(
            displayProbe: ObsSourceColorSpace.Srgb, hookedGame: ObsSourceColorSpace.Extended709);

        Assert.Equal(ObsSourceColorSpace.Srgb, space);
    }

    [Theory]
    [InlineData(ObsSourceColorSpace.Extended709)]
    [InlineData(ObsSourceColorSpace.Scrgb709)]
    [InlineData(ObsSourceColorSpace.Srgb)]
    public void WithoutADisplayProbe_TheHookedGameDecides(ObsSourceColorSpace hooked)
    {
        Assert.Equal(hooked, RecorderColourPolicy.CapturedColourSpace(displayProbe: null, hookedGame: hooked));
    }

    [Fact]
    public void WithNeitherAnswer_TheColourSpaceIsUnknown()
    {
        Assert.Null(RecorderColourPolicy.CapturedColourSpace(displayProbe: null, hookedGame: null));
    }
}
