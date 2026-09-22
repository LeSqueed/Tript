// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using System.Text;

namespace Tript.Media;

public static class FfmpegRunner
{
    // ffmpeg prints its banner and stream table first and the actual error last, so the end is what
    // explains a failure. Capped so one broken file cannot write megabytes into the log.
    internal static string Tail(string standardError, int maxChars = 600)
    {
        var trimmed = standardError.TrimEnd();
        return trimmed.Length <= maxChars ? trimmed : "..." + trimmed[^maxChars..];
    }

    // A fixed deadline would kill long, healthy encodes: a clip cut from a four-hour session can take
    // many minutes. ffmpeg prints a stats line to stderr roughly twice a second while it works, so no
    // output at all for this long means it is wedged rather than slow.
    internal static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromMinutes(5);

    public static void Run(string ffmpegPath, IReadOnlyList<string> args, ClipRequest request, string stage)
        => Run(ffmpegPath, args, request, stage, DefaultStallTimeout);

    internal static void Run(string ffmpegPath, IReadOnlyList<string> args, ClipRequest request, string stage,
        TimeSpan stallTimeout)
    {
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

        ProcessPipes.Adopt(process);

        var stderr = new StringBuilder();
        var lastActivity = Environment.TickCount64;
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            Volatile.Write(ref lastActivity, Environment.TickCount64);
            lock (stderr) stderr.AppendLine(e.Data);
            Report(request, ClipProgress.At(stage, e.Data));
        };

        try
        {
            try { process.StandardInput.Close(); } catch (IOException) {  }
            process.BeginErrorReadLine();
            var stdoutTask = ProcessPipes.BeginRead(process.StandardOutput);

            while (!process.WaitForExit(1000))
            {
                if (Environment.TickCount64 - Volatile.Read(ref lastActivity) > stallTimeout.TotalMilliseconds)
                {
                    ProcessPipes.KillQuietly(process);
                    throw new ClipEncodeException(
                        $"ffmpeg made no progress during {stage} for {stallTimeout.TotalSeconds:0}s and was stopped.", -1);
                }
            }

            // The timed overload can return before the async stderr handler has drained.
            process.WaitForExit();
            ProcessPipes.Settle(stdoutTask);
        }
        catch
        {
            // Anything thrown between Start and exit unwound through `using var process`, which
            // disposes the Process object but does not kill the child. ffmpeg then kept running and
            // kept its output file locked, with nothing tracking it.
            ProcessPipes.KillQuietly(process);
            throw;
        }

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

        ProcessPipes.Adopt(process);
        try { process.StandardInput.Close(); } catch (IOException) {  }

        var stdout = ProcessPipes.BeginRead(process.StandardOutput);
        var stderr = ProcessPipes.BeginRead(process.StandardError);

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            ProcessPipes.KillQuietly(process);

            ProcessPipes.Settle(stdout, stderr);
            return FfmpegOutcome.TimedOut(timeout);
        }

        ProcessPipes.Settle(stdout, stderr);

        return new FfmpegOutcome(
            Completed: true,
            ExitCode: process.ExitCode,
            StandardError: ProcessPipes.TextOf(stderr));
    }

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

public readonly record struct FfmpegOutcome(bool Completed, int ExitCode, string StandardError)
{
    public bool Succeeded => Completed && ExitCode == 0;

    internal static FfmpegOutcome NotStarted(string detail) => new(false, -1, detail);

    internal static FfmpegOutcome TimedOut(TimeSpan timeout) =>
        new(false, -1, $"ffmpeg did not exit within {timeout.TotalSeconds:0.#}s and was killed.");
}
