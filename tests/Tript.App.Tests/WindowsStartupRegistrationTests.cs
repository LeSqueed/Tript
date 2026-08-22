// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.App.Tests;

public sealed class WindowsStartupRegistrationTests
{
    [Fact]
    public void BuildCommandLine_QuotesTheExecutableAndMarksStartupLaunch()
    {
        Assert.Equal("\"C:\\Program Files\\Tript\\Tript.Shell.exe\" --startup",
            Tript.Shell.WindowsStartupRegistration.BuildCommandLine(
                "C:\\Program Files\\Tript\\Tript.Shell.exe"));
    }

    [Fact]
    public void EnablingStartup_WritesThePerUserEntry()
    {
        var store = new FakeStartupEntryStore();
        var registration = new Tript.Shell.WindowsStartupRegistration(store, isWindows: true);

        registration.Apply(true, "C:\\Tript\\Tript.Shell.exe");

        Assert.Equal("\"C:\\Tript\\Tript.Shell.exe\" --startup", store.Values[Tript.Shell.WindowsStartupRegistration.EntryName]);
    }

    [Fact]
    public void DisablingStartup_RemovesThePerUserEntry()
    {
        var store = new FakeStartupEntryStore();
        store.Values[Tript.Shell.WindowsStartupRegistration.EntryName] = "old";
        var registration = new Tript.Shell.WindowsStartupRegistration(store, isWindows: true);

        registration.Apply(false, "C:\\Tript\\Tript.Shell.exe");

        Assert.Empty(store.Values);
    }

    [Fact]
    public void NonWindows_IsANoop()
    {
        var store = new FakeStartupEntryStore();
        var registration = new Tript.Shell.WindowsStartupRegistration(store, isWindows: false);

        registration.Apply(true, "/opt/tript/Tript.Shell");

        Assert.Empty(store.Values);
    }

    [Fact]
    public void BuildLibraryUrl_AlwaysStartsInTheLibraryWithoutChangingTheLaunchToken()
    {
        Assert.Equal("http://localhost:2882/?k=token#library",
            Tript.Shell.Program.BuildLibraryUrl("http://localhost:2882/?k=token"));
    }

    [Fact]
    public void BuildSettingsUrl_UsesAUniqueFragmentWithoutChangingTheLaunchToken()
    {
        var first = Tript.Shell.Program.BuildSettingsUrl("http://localhost:2882/?k=token");
        var second = Tript.Shell.Program.BuildSettingsUrl("http://localhost:2882/?k=token");

        Assert.StartsWith("http://localhost:2882/?k=token#settings-", first);
        Assert.StartsWith("http://localhost:2882/?k=token#settings-", second);
        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData(true, Tript.App.NotificationKind.RecordingStarted)]
    [InlineData(true, Tript.App.NotificationKind.RecordingStopped)]
    [InlineData(true, Tript.App.NotificationKind.Error)]
    [InlineData(true, Tript.App.NotificationKind.Recovery)]
    public void EnabledNotificationKinds_AreAccepted(bool enabled, Tript.App.NotificationKind kind)
    {
        var settings = new Tript.Settings.NotificationSettings
        {
            Enabled = enabled,
            RecordingStarted = true,
            RecordingStopped = true,
            Errors = true,
            Recovery = true,
        };

        Assert.True(Tript.Shell.Program.NotificationEnabled(settings, kind));
    }

    [Fact]
    public void TrayVisibilityMenu_MapsToShowOrHide()
    {
        Assert.Equal(Tript.Shell.TrayCommand.Hide,
            Tript.Shell.WindowsTrayPresence.MenuVisibilityCommand(visible: true));
        Assert.Equal(Tript.Shell.TrayCommand.Show,
            Tript.Shell.WindowsTrayPresence.MenuVisibilityCommand(visible: false));
    }

    private sealed class FakeStartupEntryStore : Tript.Shell.IStartupEntryStore
    {
        internal Dictionary<string, string> Values { get; } = new();

        public void Set(string name, string commandLine) => Values[name] = commandLine;

        public void Delete(string name) => Values.Remove(name);
    }
}
