// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using Tript.Settings;
using Tript.Shell;
using Tript.Shell.Linux;
using Tript.TestSupport;
using Xunit;

namespace Tript.App.Tests;

public sealed class LinuxNotificationsTests
{
    [Fact]
    public void ANotification_IsSentAsTriptWithItsIconAndNoSoundOfItsOwn()
    {
        var call = LinuxNotifications.NotifyCall("Recording started", "Elden Ring", "/opt/tript/wwwroot/tript.png",
            markup: false);

        Assert.Equal(("org.freedesktop.Notifications", "/org/freedesktop/Notifications", "Notify"),
            (call.Destination, call.ObjectPath, call.Method));
        var parameters = GVariantPrinted.Parse(call.Parameters);
        Assert.Equal("Tript", parameters[0]!.AsString());
        Assert.Equal(0u, parameters[1]!.AsUInt32());
        Assert.Equal("/opt/tript/wwwroot/tript.png", parameters[2]!.AsString());
        Assert.Equal("Recording started", parameters[3]!.AsString());
        Assert.Equal("Elden Ring", parameters[4]!.AsString());
        Assert.Empty(parameters[5]!.Items);
        Assert.True(parameters[6]!.Lookup("suppress-sound")!.AsBoolean());
        Assert.Equal("-1", parameters[7]!.Scalar);
    }

    [Fact]
    public void AMissingIcon_IsSentAsAnEmptyIconName() =>
        Assert.Equal(string.Empty,
            GVariantPrinted.Parse(LinuxNotifications.NotifyCall("t", "b", null, markup: false).Parameters)[2]!
                .AsString());

    [Fact]
    public void ABodyShownAsMarkup_IsEscaped()
    {
        var body = GVariantPrinted.Parse(
            LinuxNotifications.NotifyCall("Tript error", "Can't open <C:\\a & b>", null, markup: true).Parameters)[4]!;

        Assert.Equal("Can't open &lt;C:\\a &amp; b&gt;", body.AsString());
    }

    [Fact]
    public void ABodyShownAsPlainText_IsLeftAlone() =>
        Assert.Equal("a & <b>", GVariantPrinted.Parse(
            LinuxNotifications.NotifyCall("t", "a & <b>", null, markup: false).Parameters)[4]!.AsString());

    [Fact]
    public void AFocusedWindow_SuppressesBothTheNotificationAndItsSound() =>
        Assert.Equal(new NotificationPlan(false, false),
            ShellNotifications.Plan(new NotificationSettings(), NotificationKind.RecordingStarted, windowFocused: true,
                canNotify: true, canPlaySound: true));

    [Fact]
    public void DisabledNotifications_SuppressEverything() =>
        Assert.Equal(new NotificationPlan(false, false),
            ShellNotifications.Plan(new NotificationSettings { Enabled = false }, NotificationKind.Error,
                windowFocused: false, canNotify: true, canPlaySound: true));

    [Fact]
    public void EachKind_FollowsItsOwnNotificationAndSoundSettings()
    {
        var settings = new NotificationSettings { RecordingStopped = false, RecordingStoppedSound = true, ErrorsSound = false };

        Assert.Equal(new NotificationPlan(false, true),
            ShellNotifications.Plan(settings, NotificationKind.RecordingStopped, false, true, true));
        Assert.Equal(new NotificationPlan(true, false),
            ShellNotifications.Plan(settings, NotificationKind.Error, false, true, true));
        Assert.Equal(new NotificationPlan(true, false),
            ShellNotifications.Plan(settings, NotificationKind.UpdateReady, false, true, true));
    }

    [Fact]
    public void AMissingNotificationServerOrPlayer_OnlyDropsWhatItWouldHaveDone()
    {
        Assert.Equal(new NotificationPlan(false, true),
            ShellNotifications.Plan(new NotificationSettings(), NotificationKind.RecordingStarted, false,
                canNotify: false, canPlaySound: true));
        Assert.Equal(new NotificationPlan(true, false),
            ShellNotifications.Plan(new NotificationSettings(), NotificationKind.RecordingStarted, false,
                canNotify: true, canPlaySound: false));
    }

    [Fact]
    public void SoundCues_UseTheSameFilesAsWindows()
    {
        Assert.Equal(Path.Combine("web", "sounds", "recording-started.wav"),
            NotificationSoundFiles.PathFor(NotificationKind.RecordingStarted, "web"));
        Assert.Equal(Path.Combine("web", "sounds", "recording-stopped.wav"),
            NotificationSoundFiles.PathFor(NotificationKind.RecordingStopped, "web"));
        Assert.Equal(Path.Combine("web", "sounds", "error.wav"),
            NotificationSoundFiles.PathFor(NotificationKind.Error, "web"));
        Assert.Null(NotificationSoundFiles.PathFor(NotificationKind.UpdateReady, "web"));
    }

    [LinuxFact]
    public void PipeWiresPlayer_IsPreferredOverPulseAudios()
    {
        var existing = new HashSet<string> { "/usr/bin/paplay", "/usr/local/bin/pw-play", "/usr/bin/pw-play" };

        var players = LinuxSoundPlayer.FindPlayers("/usr/local/bin:/usr/bin", existing.Contains);

        Assert.Equal(["/usr/local/bin/pw-play", "/usr/bin/paplay"], players);
    }

    [Fact]
    public void NoPlayerOnThePath_LeavesSoundsUnavailable()
    {
        var player = new LinuxSoundPlayer(LinuxSoundPlayer.FindPlayers("/usr/bin", _ => false));

        Assert.False(player.Available);
        Assert.Empty(LinuxSoundPlayer.FindPlayers(null, _ => true));
    }

    [SkippableFact]
    public async Task APlayerThatFails_HandsTheSoundToTheNextOne()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "The fallback chain starts real child processes.");

        var tried = new List<string>();
        var nextStarted = new TaskCompletionSource();
        var player = new LinuxSoundPlayer(["broken", "missing", "working"], (name, file) =>
        {
            lock (tried)
                tried.Add(name);
            return name switch
            {
                "broken" => Process.Start("sh", ["-c", "exit 3"]),
                "missing" => throw new System.ComponentModel.Win32Exception(2),
                _ => Complete(nextStarted),
            };
        });

        player.PlayWith(0, "/sounds/recording-started.wav");

        await nextStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        lock (tried)
            Assert.Equal(["broken", "missing", "working"], tried);
    }

    [SkippableFact]
    public async Task APlayerThatSucceeds_IsNotFollowedByAnother()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "The fallback chain starts real child processes.");

        var tried = new List<string>();
        var player = new LinuxSoundPlayer(["working", "unused"], (name, _) =>
        {
            lock (tried)
                tried.Add(name);
            return Process.Start("sh", ["-c", "exit 0"]);
        });

        player.PlayWith(0, "/sounds/error.wav");
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        lock (tried)
            Assert.Equal(["working"], tried);
    }

    private static Process? Complete(TaskCompletionSource started)
    {
        started.TrySetResult();
        return null;
    }
}
