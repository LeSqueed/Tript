// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using System.Text;

namespace Tript.Media;

// Runs an ffmpeg process with a prepared argument list. Two runners, one per caller shape: Run
// streams stderr as clip progress and waits for as long as the encode takes; RunBounded caps the
// run with a timeout and reports the outcome instead of throwing (see its comment for why the clip
// runner cannot serve a request thread).
public static class FfmpegRunner
{
    public static void Run(string ffmpegPath, IReadOnlyList<string> args, ClipRequest request, string stage)
    {
        // -nostdin: never read the terminal, whatever this process inherited. -y: an ffmpeg that
        // finds its output file already there otherwise stops to ask "Overwrite? [y/N]" and, with
        // no answer coming, waits forever. A clip's name is derived from its source and its region
        // (or its request id), so a file already at that name is a previous attempt at this exact
        // clip — including the partial one a killed encode leaves behind.
        var arguments = new List<string>(args.Count + 2) { "-nostdin", "-y" };
        arguments.AddRange(args);

        Report(request, ClipProgress.At(stage, string.Join(' ', arguments)));

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

        foreach (var arg in arguments)
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

        // Belt and braces with -nostdin: close stdin so any read of it sees EOF immediately.
        try { process.StandardInput.Close(); } catch (IOException) { /* the child exited first */ }

        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            lock (stderr) stderr.AppendLine(e.Data);
            Report(request, ClipProgress.At(stage, e.Data));
        };

        process.BeginErrorReadLine();

        // Drain stdout (unused for clips) so the child cannot block on a full pipe.
        var stdoutTask = ProcessPipes.BeginRead(process.StandardOutput);
        process.WaitForExit();
        ProcessPipes.Settle(stdoutTask);

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
    // there — a ten-minute source legitimately encodes for minutes.
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
        var stdout = ProcessPipes.BeginRead(process.StandardOutput);
        var stderr = ProcessPipes.BeginRead(process.StandardError);

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            ProcessPipes.KillQuietly(process);

            // The kill closes the pipes, so the reads end here rather than outliving the Process
            // this using-block is about to dispose.
            ProcessPipes.Settle(stdout, stderr);
            return FfmpegOutcome.TimedOut(timeout);
        }

        // The exit is observed; give the two reads a moment to flush and then take whatever they
        // have. A read that somehow never completes must not turn a finished process into a hang.
        ProcessPipes.Settle(stdout, stderr);

        return new FfmpegOutcome(
            Completed: true,
            ExitCode: process.ExitCode,
            StandardError: ProcessPipes.TextOf(stderr));
    }

    // The progress sink belongs to the caller and, for the stderr lines, is invoked on a Process
    // event thread. An exception out of it (a closed websocket, say) would be thrown on that thread
    // and take the host down with it, so a broken sink is dropped rather than allowed to fail a
    // clip that is otherwise fine.
    private static void Report(ClipRequest request, ClipProgress progress)
    {
        try
        {
            request.Progress?.Invoke(progress);
        }
        catch (Exception)
        {
        }
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
