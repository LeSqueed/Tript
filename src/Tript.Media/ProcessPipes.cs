// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;

namespace Tript.Media;

internal static class ProcessPipes
{
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(2);

    internal static Task<string> BeginRead(StreamReader reader)
    {
        var read = reader.ReadToEndAsync();

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
        }
    }
}
