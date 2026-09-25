// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;
using Xunit;

namespace Tript.App.Tests;

public sealed class NixPlatformSelectorTests
{
    [Fact]
    public void AWaylandSessionWithThePipeWirePlugin_RunsLibobsOnWayland() =>
        Assert.Equal(ObsNixPlatform.Wayland,
            NixPlatformSelector.Select(null, "wayland-1", ":1", pipeWireModulePresent: true));

    [Fact]
    public void AWaylandSessionWithoutThePipeWirePlugin_FallsBackToXWayland() =>
        Assert.Equal(ObsNixPlatform.X11Egl,
            NixPlatformSelector.Select(null, "wayland-1", ":1", pipeWireModulePresent: false));

    [Fact]
    public void AWaylandSessionWithNoXWayland_StaysOnWaylandEvenWithoutThePipeWirePlugin() =>
        Assert.Equal(ObsNixPlatform.Wayland,
            NixPlatformSelector.Select(null, "wayland-1", null, pipeWireModulePresent: false));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnX11Session_RunsLibobsOnX11(bool pipeWire) =>
        Assert.Equal(ObsNixPlatform.X11Egl,
            NixPlatformSelector.Select(null, null, ":0", pipeWire));

    [Theory]
    [InlineData("x11", ObsNixPlatform.X11Egl)]
    [InlineData("X11", ObsNixPlatform.X11Egl)]
    [InlineData("wayland", ObsNixPlatform.Wayland)]
    public void AnExplicitRequest_WinsOverTheSession(string requested, ObsNixPlatform expected)
    {
        Assert.Equal(expected, NixPlatformSelector.Select(requested, "wayland-1", ":1", pipeWireModulePresent: true));
        Assert.Equal(expected, NixPlatformSelector.Select(requested, null, ":0", pipeWireModulePresent: false));
    }

    [Fact]
    public void AnUnrecognisedRequest_IsIgnored() =>
        Assert.Equal(ObsNixPlatform.Wayland,
            NixPlatformSelector.Select("mir", "wayland-1", ":1", pipeWireModulePresent: true));
}
