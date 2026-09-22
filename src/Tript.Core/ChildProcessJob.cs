// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Tript.Core;

// Every graceful exit path kills Tript's ffmpeg and training children, but none of those paths run
// when Tript crashes, when the shell falls back to Environment.Exit, or when the process is killed.
// The children then kept running: ffmpeg holding its output file, and a training run holding GPU
// memory for hours. A job object with kill-on-close fixes that at the OS level. The job handle is
// deliberately never closed: Windows closes it when this process ends, however it ends, and that
// is what kills the children.
//
// Only processes that must not outlive Tript belong here. Explorer, the browser and anything else
// opened for the user must never be tracked, or quitting Tript would close them too.
public static class ChildProcessJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private static readonly Lazy<nint> Job = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static nint Handle => Job.Value;

    public static void Track(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!OperatingSystem.IsWindows())
            return;

        var job = Job.Value;
        if (job == nint.Zero)
            return;

        try
        {
            if (!AssignProcessToJobObject(job, process.Handle))
            {
                Diagnostics.Report(DiagnosticLevel.Warning,
                    $"Could not tie child process {process.Id} to Tript's lifetime (Win32 error {Marshal.GetLastWin32Error()}); "
                    + "it may outlive a crash");
            }
        }
        catch (InvalidOperationException)
        {
            // The child already exited, so there is nothing left to tie.
        }
    }

    private static nint Create()
    {
        if (!OperatingSystem.IsWindows())
            return nint.Zero;

        var job = CreateJobObjectW(nint.Zero, null);
        if (job == nint.Zero)
        {
            Diagnostics.Report(DiagnosticLevel.Warning,
                $"Could not create the child-process job (Win32 error {Marshal.GetLastWin32Error()}); "
                + "child processes may outlive a crash");
            return nint.Zero;
        }

        var limits = new ExtendedLimitInformation
        {
            BasicLimitInformation = new BasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose },
        };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits,
                (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            Diagnostics.Report(DiagnosticLevel.Warning,
                $"Could not configure the child-process job (Win32 error {Marshal.GetLastWin32Error()}); "
                + "child processes may outlive a crash");
            CloseHandle(job);
            return nint.Zero;
        }

        return job;
    }

    // JOBOBJECT_BASIC_LIMIT_INFORMATION. Field types, not explicit offsets, carry the x64 alignment;
    // the size is pinned by a test, because a short struct here is silent memory corruption.
    [StructLayout(LayoutKind.Sequential)]
    internal struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObjectW(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(nint job, int infoClass,
        ref ExtendedLimitInformation info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
