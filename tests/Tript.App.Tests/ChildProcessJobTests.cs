// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using System.Runtime.InteropServices;
using Tript.Core;
using Xunit;

namespace Tript.App.Tests;

public sealed class ChildProcessJobTests
{
    // SetInformationJobObject is handed Marshal.SizeOf of this struct. A struct shorter than the
    // native JOBOBJECT_EXTENDED_LIMIT_INFORMATION is exactly the PROPVARIANT class of bug: the call
    // fails, or reads past the managed value. 144 bytes is the x64 layout.
    [Fact]
    public void TheLimitStructureMatchesTheNativeLayout()
    {
        if (IntPtr.Size != 8)
            return;

        Assert.Equal(64, Marshal.SizeOf<ChildProcessJob.BasicLimitInformation>());
        Assert.Equal(144, Marshal.SizeOf<ChildProcessJob.ExtendedLimitInformation>());
    }

    [SkippableFact]
    public void TheJobIsCreatedAndConfigured()
    {
        if (!OperatingSystem.IsWindows())
            throw new SkipException("Job objects are Windows-only.");

        Assert.NotEqual(IntPtr.Zero, ChildProcessJob.Handle);
    }

    // Asks about this specific job rather than "any job": the test runner may already run inside a
    // job of its own, and then every child would pass a looser check regardless.
    [SkippableFact]
    public void ATrackedChildIsInTriptsJob()
    {
        if (!OperatingSystem.IsWindows())
            throw new SkipException("Job objects are Windows-only.");

        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        try
        {
            ChildProcessJob.Track(child);

            Assert.True(IsProcessInJob(child.Handle, ChildProcessJob.Handle, out var inJob));
            Assert.True(inJob, "the child was not assigned to Tript's kill-on-close job");
        }
        finally
        {
            child.Kill(entireProcessTree: true);
        }
    }

    [SkippableFact]
    public void TrackingAChildThatAlreadyExitedIsHarmless()
    {
        if (!OperatingSystem.IsWindows())
            throw new SkipException("Job objects are Windows-only.");

        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        child.WaitForExit();

        ChildProcessJob.Track(child);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);
}
