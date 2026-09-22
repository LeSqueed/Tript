// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using Tript.Core;

namespace Tript.Media;

internal static class ProcessPipes
{
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(2);

    // A dedicated thread, not ReadToEndAsync: a process's redirected pipes are synchronous handles, so
    // the async read blocks a pool thread. With the pool busy it could start after the process had
    // exited and DrainGrace had run out, and a successful ffprobe then came back as empty output.
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

    // Called right after Start for every ffmpeg and ffprobe child. The job ties the child to Tript's
    // lifetime, so a crash or forced exit no longer leaves ffmpeg running and holding its output.
    internal static void Adopt(Process process)
    {
        ChildProcessJob.Track(process);
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
            // InvalidOperationException is the normal "already exited" case. Anything else means
            // an ffmpeg that may still be running, holding its output file, with nothing tracking it.
            if (exception is not InvalidOperationException)
                Diagnostics.Report(DiagnosticLevel.Warning, "Could not kill an ffmpeg process; it may still be running", exception);
        }
    }
}
