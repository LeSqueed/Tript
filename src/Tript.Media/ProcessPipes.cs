// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;

namespace Tript.Media;

// The pipe-draining half of running a child process, shared by the ffmpeg and ffprobe runners.
// Both reads have to be started before the wait: draining one to EOF and only then the other
// deadlocks a child that fills the pipe nobody is reading, because the child then never exits and
// the first pipe never reaches EOF either.
internal static class ProcessPipes
{
    // How long an exited (or killed) process is given to flush what is still in its pipes. A read
    // that somehow outlasts this is left to the fault observer below rather than held on to.
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(2);

    internal static Task<string> BeginRead(StreamReader reader)
    {
        var read = reader.ReadToEndAsync();

        // A read still pending when the Process is disposed faults on the closed handle. Observing
        // it here keeps that off the finalizer's unobserved-exception path, where it would be
        // rethrown against whatever thread happens to be running.
        _ = read.ContinueWith(static task => _ = task.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

        return read;
    }

    // Gives the reads a bounded moment to finish, so they are settled before the caller disposes
    // the Process they are reading from.
    internal static void Settle(params Task[] reads)
    {
        try { Task.WaitAll(reads, DrainGrace); }
        catch (AggregateException) { /* a pipe closed early; TextOf then yields empty */ }
    }

    internal static string TextOf(Task<string> read) =>
        read.IsCompletedSuccessfully ? read.Result : string.Empty;

    // entireProcessTree: neither ffmpeg nor ffprobe spawns children today, but a killed parent
    // leaving a live child holding the output file open is the failure mode that would make the
    // next attempt fail too.
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
            // Already gone, or the platform refused the kill; either way the caller only needs to
            // know the run did not complete.
        }
    }
}
