// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using Tript.TestSupport;
using Xunit;

namespace Tript.App.Tests;

public sealed class FileManagerCommandsTests
{
    private const string SpacedFile = "/home/ana/My Videos/Tript/Over watch/session 1.mp4";

    [LinuxFact]
    public void RevealFile_OnLinux_OpensTheContainingFolderAsOneArgument()
    {
        var command = FileManagerCommands.RevealFile(SpacedFile, DesktopPlatform.Linux);

        Assert.Equal("xdg-open", command.FileName);
        Assert.Equal(["/home/ana/My Videos/Tript/Over watch"], command.ArgumentList);
        Assert.Empty(command.Arguments);
        Assert.False(command.UseShellExecute);
    }

    [Fact]
    public void OpenFolder_OnLinux_PassesAPathWithSpacesAsOneArgument()
    {
        var command = FileManagerCommands.OpenFolder("/home/ana/.local/state/Tript Beta/logs", DesktopPlatform.Linux);

        Assert.Equal("xdg-open", command.FileName);
        Assert.Equal(["/home/ana/.local/state/Tript Beta/logs"], command.ArgumentList);
    }

    [Fact]
    public void RevealFile_OnMacOS_SelectsTheFileWithoutQuotingIt()
    {
        var command = FileManagerCommands.RevealFile(SpacedFile, DesktopPlatform.MacOS);

        Assert.Equal("open", command.FileName);
        Assert.Equal(["-R", SpacedFile], command.ArgumentList);
    }

    [Fact]
    public void RevealFile_OnWindows_SelectsTheFileInExplorer()
    {
        var command = FileManagerCommands.RevealFile(@"C:\Videos\Tript\say ""hi"".mp4", DesktopPlatform.Windows);

        Assert.Equal("explorer.exe", command.FileName);
        Assert.Equal(@"/select,""C:\Videos\Tript\say hi.mp4""", command.Arguments);
        Assert.True(command.UseShellExecute);
    }

    [Fact]
    public void OpenFolder_OnWindows_QuotesTheFolderForExplorer()
    {
        var command = FileManagerCommands.OpenFolder(@"C:\Users\ana\AppData\Roaming\Tript\logs", DesktopPlatform.Windows);

        Assert.Equal("explorer.exe", command.FileName);
        Assert.Equal(@"""C:\Users\ana\AppData\Roaming\Tript\logs""", command.Arguments);
        Assert.True(command.UseShellExecute);
    }

    [LinuxFact]
    public async Task Launch_LeavesNoZombieBehind_OnceTheChildExits()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"tript-reap-{Guid.NewGuid():N}");
        var startInfo = new ProcessStartInfo { FileName = "/bin/sh", UseShellExecute = false };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("echo $$ > \"$0\"");
        startInfo.ArgumentList.Add(marker);

        try
        {
            FileManagerCommands.Launch(startInfo);

            var pid = await WaitForPidAsync(marker);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (Directory.Exists($"/proc/{pid}"))
            {
                Assert.True(DateTime.UtcNow < deadline, $"child {pid} was never reaped");
                await Task.Delay(20);
            }
        }
        finally
        {
            File.Delete(marker);
        }
    }

    private static async Task<int> WaitForPidAsync(string marker)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            if (File.Exists(marker) && int.TryParse((await File.ReadAllTextAsync(marker)).Trim(), out var pid))
                return pid;
            Assert.True(DateTime.UtcNow < deadline, "the child never wrote its pid");
            await Task.Delay(20);
        }
    }
}
