// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Shell;
using Xunit;

namespace Tript.App.Tests;

public sealed class StartupFailureDialogTests
{
    private const string Message = "Tript could not start.\n\nlibobs.so.0: cannot open shared object file";

    [Fact]
    public void WithZenityInstalled_TheErrorIsShownAsPlainTextSoMarkupInPathsCannotBreakIt()
    {
        var command = StartupFailureDialog.Choose(Message, tool => tool is "zenity" or "kdialog");

        Assert.NotNull(command);
        Assert.Equal("zenity", command.FileName);
        Assert.Contains("--no-markup", command.Arguments);
        Assert.Contains($"--text={Message}", command.Arguments);
    }

    [Fact]
    public void OnKdeWithoutZenity_KdialogShowsTheError()
    {
        var command = StartupFailureDialog.Choose(Message, tool => tool == "kdialog");

        Assert.Equal("kdialog", command?.FileName);
        Assert.Equal(["--title", "Tript", "--error", Message], command!.Arguments);
    }

    [Fact]
    public void WithOnlyANotificationTool_TheErrorBecomesACriticalNotification()
    {
        var command = StartupFailureDialog.Choose(Message, tool => tool == "notify-send");

        Assert.Equal("notify-send", command?.FileName);
        Assert.Contains("--urgency=critical", command!.Arguments);
        Assert.Equal(Message, command.Arguments[^1]);
    }

    [Fact]
    public void WithNoDialogToolAtAll_NothingIsLaunched() =>
        Assert.Null(StartupFailureDialog.Choose(Message, _ => false));
}
