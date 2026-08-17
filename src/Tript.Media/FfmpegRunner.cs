// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using System.Text;

namespace Tript.Media;

// Runs an ffmpeg process with a prepared argument list. Two runners, one per caller shape: Run
// streams stderr as clip progress and waits for as long as the encode takes; RunBounded caps the
// run with a timeout and reports the outcome instead of throwing (see its comment for why the clip
// runner cannot serve a request thread).
//
// The argument list is passed via ProcessStartInfo.ArgumentList, which handles per-argument quoting
// for the platform — a single Arguments string would be split by the shell or, with UseShellExecute
// false, passed to execve with literal quote characters (measured: ffmpeg received the quotes and
// failed with exit code 234).
public static class FfmpegRunner
{
    public static void Run(string ffmpegPath, IReadOnlyList<string> args, ClipRequest request, string stage)
    {
        request.Progress?.Invoke(ClipProgress.At(stage, string.Join(' ', args)));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            }
        };

        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        try
        {
            if (!process.Start())
                throw new ClipSourceException($"Failed to start ffmpeg: {ffmpegPath}");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ClipSourceException($"Failed to start ffmpeg at '{ffmpegPath}': {ex.Message}", ex);
        }

        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            lock (stderr) stderr.AppendLine(e.Data);
            request.Progress?.Invoke(ClipProgress.At(stage, e.Data));
        };

        process.BeginErrorReadLine();

        // Drain stdout (unused for clips) so the child cannot block on a full pipe.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        process.WaitForExit();
        stdoutTask.Wait();

        if (process.ExitCode != 0)
        {
            string detail;
            lock (stderr) detail = stderr.ToString();
            throw new ClipEncodeException(
                $"ffmpeg failed during {stage} with exit code {process.ExitCode}."
                + (string.IsNullOrWhiteSpace(detail) ? string.Empty : " " + detail.Trim()),
                process.ExitCode);
        }
    }

    // A time-bounded run that reports its outcome instead of throwing. Run (above) is the clip
    // path's runner: it streams progress and waits as long as the encode takes, which is correct
    // there — a ten-minute source legitimately encodes for minutes. The thumbnail path cannot use
    // it. It runs on an HttpListener worker while a browser request is open, so an ffmpeg that
    // never exits (a truncated file it keeps waiting on, a wedged hardware decoder) would hold the
    // request and eventually the pool, and an exception thrown there would surface as a 500 rather
    // than as "no thumbnail". Both runners live in this class so there stays one place ffmpeg is
    // spawned and one set of ProcessStartInfo conventions (ArgumentList, no shell, no window).
    //
    // Callers must not trust ExitCode alone: ffmpeg exits 0 with an empty stderr when a seek lands
    // past the end of a file, producing no output at all. Verify the output file exists and is
    // non-empty.
    public static FfmpegOutcome RunBounded(string ffmpegPath, IReadOnlyList<string> args, TimeSpan timeout)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            }
        };

        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        try
        {
            if (!process.Start())
                return FfmpegOutcome.NotStarted($"Failed to start ffmpeg: {ffmpegPath}");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                                             or InvalidOperationException or IOException)
        {
            return FfmpegOutcome.NotStarted($"Failed to start ffmpeg at '{ffmpegPath}': {exception.Message}");
        }

        // Close stdin immediately so the child reads EOF. ffmpeg's interactive handler reads stdin
        // for the 'q' key; with stdin inherited from a service process it can block there. -nostdin
        // covers the same ground from the argument side, and both are cheap.
        try { process.StandardInput.Close(); } catch (IOException) { /* the child exited first */ }

        // Both pipes are drained concurrently with the wait: a child blocked on a full stderr pipe
        // would never exit and the timeout below would fire for the wrong reason.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try
            {
                // entireProcessTree: ffmpeg spawns no children today, but a killed parent leaving a
                // live child holding the output file open is the failure mode that would make the
                // next attempt fail too.
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
            catch (Exception exception) when (exception is InvalidOperationException
                                                 or System.ComponentModel.Win32Exception
                                                 or NotSupportedException)
            {
                // Already gone, or the platform refused the kill; either way the caller only needs
                // to know the run did not complete.
            }

            return FfmpegOutcome.TimedOut(timeout);
        }

        // The exit is observed; give the two reads a moment to flush and then take whatever they
        // have. A read that somehow never completes must not turn a finished process into a hang.
        try { Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { /* a pipe closed early; the text below is then just empty */ }

        return new FfmpegOutcome(
            Completed: true,
            ExitCode: process.ExitCode,
            StandardError: stderr.IsCompletedSuccessfully ? stderr.Result : string.Empty);
    }
}

// What a bounded ffmpeg run did. Completed false means the process never ran or was killed on the
// timeout; ExitCode is only meaningful when Completed is true — and even then it is not proof of
// output (an out-of-range seek exits 0 having written nothing).
public readonly record struct FfmpegOutcome(bool Completed, int ExitCode, string StandardError)
{
    public bool Succeeded => Completed && ExitCode == 0;

    internal static FfmpegOutcome NotStarted(string detail) => new(false, -1, detail);

    internal static FfmpegOutcome TimedOut(TimeSpan timeout) =>
        new(false, -1, $"ffmpeg did not exit within {timeout.TotalSeconds:0.#}s and was killed.");
}
