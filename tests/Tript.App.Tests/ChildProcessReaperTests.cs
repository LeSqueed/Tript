// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using Tript.Core;
using Tript.TestSupport;
using Xunit;

namespace Tript.App.Tests;

public sealed class ChildProcessReaperTests
{
    private static Process Start(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName) { UseShellExecute = false, RedirectStandardOutput = true };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return Process.Start(startInfo)!;
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var state = stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[0];
            return state != "Z" && state != "X";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void AssertGone(int pid)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (IsAlive(pid))
        {
            Assert.True(DateTime.UtcNow < deadline, $"process {pid} is still running");
            Thread.Sleep(20);
        }
    }

    [LinuxFact]
    public void Dispose_KillsATrackedChild()
    {
        using var child = Start("sleep", "30");
        var reaper = new ChildProcessReaper();
        reaper.Track(child);

        reaper.Dispose();

        Assert.True(child.WaitForExit(TimeSpan.FromSeconds(10)), "the tracked child outlived the reaper");
    }

    [LinuxFact]
    public void Dispose_KillsTheChildsOwnChildrenToo()
    {
        using var child = Start("/bin/sh", "-c", "sleep 30 & echo $!; wait");
        var grandchild = int.Parse(child.StandardOutput.ReadLine()!);
        var reaper = new ChildProcessReaper();
        reaper.Track(child);

        reaper.Dispose();

        AssertGone(child.Id);
        AssertGone(grandchild);
    }

    [LinuxFact]
    public void Dispose_KillsAChild_WhoseCallerAlreadyReleasedItsHandle()
    {
        var child = Start("sleep", "30");
        var pid = child.Id;
        var reaper = new ChildProcessReaper();
        reaper.Track(child);
        child.Dispose();

        reaper.Dispose();

        AssertGone(pid);
    }

    [LinuxFact]
    public void Track_AfterDispose_KillsTheChildAtOnce()
    {
        using var child = Start("sleep", "30");
        var reaper = new ChildProcessReaper();
        reaper.Dispose();

        reaper.Track(child);

        Assert.True(child.WaitForExit(TimeSpan.FromSeconds(10)), "a child tracked after disposal kept running");
    }

    [LinuxFact]
    public void Track_IgnoresAChildThatHasAlreadyExited()
    {
        using var reaper = new ChildProcessReaper();
        using var finished = Start("true");
        finished.WaitForExit();

        reaper.Track(finished);

        Assert.Equal(0, reaper.TrackedCount);
    }

    [LinuxFact]
    public void Track_ForgetsChildrenThatExitedSinceTheyWereTracked()
    {
        using var reaper = new ChildProcessReaper();
        using var first = Start("sleep", "30");
        using var second = Start("sleep", "30");

        try
        {
            reaper.Track(first);
            first.Kill();
            first.WaitForExit();

            reaper.Track(second);

            Assert.Equal(1, reaper.TrackedCount);
        }
        finally
        {
            second.Kill();
        }
    }
}
