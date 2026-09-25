// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Shell.Linux;
using Xunit;

namespace Tript.App.Tests;

public sealed class LinuxHotkeyBackendsTests
{
    [Fact]
    public void AWaylandSessionWithThePortal_UsesThePortalWithoutOpeningX11()
    {
        var x11Opened = false;

        var backend = LinuxHotkeyBackends.Choose(waylandSession: true, x11Display: true,
            portalAvailable: () => true, x11Opens: () => x11Opened = true);

        Assert.Equal(LinuxHotkeyBackend.Portal, backend);
        Assert.False(x11Opened);
    }

    [Fact]
    public void ThePortal_TellsTheUserTheDesktopOwnsTheTriggers()
    {
        var availability = LinuxHotkeyBackends.Describe(LinuxHotkeyBackend.Portal);

        Assert.True(availability.Available);
        Assert.Contains("desktop", availability.Note);
    }

    [Fact]
    public void ThePortal_MarksTheShortcutsAsOwnedByTheDesktop() =>
        Assert.True(LinuxHotkeyBackends.Describe(LinuxHotkeyBackend.Portal).ManagedByDesktop);

    [Theory]
    [InlineData(1u, false)]
    [InlineData(2u, true)]
    [InlineData(3u, true)]
    public void OnlyAPortalThatCanConfigureShortcuts_OffersToOpenTheDesktopsSettings(uint version, bool configurable) =>
        Assert.Equal(configurable, LinuxHotkeyBackends.Describe(LinuxHotkeyBackend.Portal, version).Configurable);

    [Fact]
    public void KeyGrabs_AreOwnedByTript()
    {
        foreach (var backend in new[] { LinuxHotkeyBackend.X11, LinuxHotkeyBackend.XWayland })
        {
            var availability = LinuxHotkeyBackends.Describe(backend, portalVersion: 2);

            Assert.False(availability.ManagedByDesktop);
            Assert.False(availability.Configurable);
        }
    }

    [Fact]
    public void AWaylandSessionWithoutThePortal_FallsBackToXWaylandWithANote()
    {
        var backend = LinuxHotkeyBackends.Choose(waylandSession: true, x11Display: true,
            portalAvailable: () => false, x11Opens: () => true);

        Assert.Equal(LinuxHotkeyBackend.XWayland, backend);
        var availability = LinuxHotkeyBackends.Describe(backend);
        Assert.True(availability.Available);
        Assert.NotNull(availability.Note);
    }

    [Fact]
    public void AnX11Session_GrabsKeysWithoutAskingThePortal()
    {
        var portalAsked = false;

        var backend = LinuxHotkeyBackends.Choose(waylandSession: false, x11Display: true,
            portalAvailable: () => portalAsked = true, x11Opens: () => true);

        Assert.Equal(LinuxHotkeyBackend.X11, backend);
        Assert.False(portalAsked);
        Assert.Equal(new HotkeyAvailability(true, null), LinuxHotkeyBackends.Describe(backend));
    }

    [Fact]
    public void AnX11SessionWhoseDisplayWillNotOpen_TriesThePortal() =>
        Assert.Equal(LinuxHotkeyBackend.Portal, LinuxHotkeyBackends.Choose(waylandSession: false, x11Display: true,
            portalAvailable: () => true, x11Opens: () => false));

    [Fact]
    public void NoDisplayAndNoPortal_LeavesHotkeysUnavailable()
    {
        var x11Opened = false;

        var backend = LinuxHotkeyBackends.Choose(waylandSession: true, x11Display: false,
            portalAvailable: () => false, x11Opens: () => x11Opened = true);

        Assert.Equal(LinuxHotkeyBackend.None, backend);
        Assert.False(x11Opened);
        Assert.False(LinuxHotkeyBackends.Describe(backend).Available);
    }

    [Theory]
    [InlineData("wayland-0", null, true)]
    [InlineData(null, "wayland", true)]
    [InlineData(null, "Wayland", true)]
    [InlineData(null, "x11", false)]
    [InlineData("", null, false)]
    public void AWaylandSession_IsRecognisedFromTheEnvironment(string? waylandDisplay, string? sessionType,
        bool wayland)
    {
        var environment = new Dictionary<string, string?>
        {
            ["WAYLAND_DISPLAY"] = waylandDisplay,
            ["XDG_SESSION_TYPE"] = sessionType,
        };

        Assert.Equal(wayland, LinuxHotkeyBackends.IsWaylandSession(name => environment.GetValueOrDefault(name)));
    }
}
