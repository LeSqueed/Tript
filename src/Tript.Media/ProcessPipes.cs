// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using Tript.Core;

namespace Tript.Media;

internal static class ProcessPipes
{
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(2);

    // A dedicated thread: redirected pipes are synchronous, and a starved pool thread could start reading
    // only after DrainGrace ran out, turning a successful ffprobe into empty output.
    internal static Task<string> BeginRead(StreamReader reader)
    {
        var read = Task.Factory.StartNew(reader.ReadToEnd, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);

        _ = read.ContinueWith(static task => _ = task.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

        return read;
    }

    internal static void Settle(params Task[] reads)
    {
        try { Task.WaitAll(reads, DrainGrace); }
        catch (AggregateException) {  }
    }

    internal static string TextOf(Task<string> read) =>
        read.IsCompletedSuccessfully ? read.Result : string.Empty;

    internal static void Start(Process process)
    {
        ChildProcessJob.StartTracked(process);
        LowerPriority(process);
    }

    internal static void LowerPriority(Process process)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                             or System.ComponentModel.Win32Exception
                                             or NotSupportedException)
        {
        }
    }

    internal static void KillQuietly(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(2000);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                             or System.ComponentModel.Win32Exception
                                             or NotSupportedException)
        {
            // InvalidOperationException just means it already exited.
            if (exception is not InvalidOperationException)
                Diagnostics.Report(DiagnosticLevel.Warning, "Could not kill an ffmpeg process; it may still be running", exception);
        }
    }
}
