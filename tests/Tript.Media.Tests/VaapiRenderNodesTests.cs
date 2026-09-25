// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Media;
using Xunit;

namespace Tript.Media.Tests;

public sealed class VaapiRenderNodesTests
{
    [Fact]
    public void Discover_ListsRenderNodesInNumericOrder()
    {
        var nodes = VaapiRenderNodes.Discover(null,
            () => ["/dev/dri/renderD129", "/dev/dri/renderD1000", "/dev/dri/renderD128"]);

        Assert.Equal(["/dev/dri/renderD128", "/dev/dri/renderD129", "/dev/dri/renderD1000"], nodes);
    }

    [Fact]
    public void Discover_IgnoresEntriesThatAreNotRenderNodes()
    {
        var nodes = VaapiRenderNodes.Discover(null,
            () => ["/dev/dri/card0", "/dev/dri/renderD128", "/dev/dri/renderDx", "/dev/dri/renderD"]);

        Assert.Equal(["/dev/dri/renderD128"], nodes);
    }

    [Fact]
    public void Discover_FindsNothing_WhenThereAreNoRenderNodes()
    {
        Assert.Empty(VaapiRenderNodes.Discover(null, () => []));
    }

    [Theory]
    [InlineData("/dev/dri/renderD130")]
    [InlineData("  /dev/dri/renderD130 ")]
    public void Discover_UsesOnlyTheOverride_WhenOneIsSet(string overrideDevice)
    {
        var nodes = VaapiRenderNodes.Discover(overrideDevice, () => ["/dev/dri/renderD128"]);

        Assert.Equal(["/dev/dri/renderD130"], nodes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Discover_IgnoresABlankOverride(string? overrideDevice)
    {
        Assert.Equal(["/dev/dri/renderD128"], VaapiRenderNodes.Discover(overrideDevice, () => ["/dev/dri/renderD128"]));
    }
}
